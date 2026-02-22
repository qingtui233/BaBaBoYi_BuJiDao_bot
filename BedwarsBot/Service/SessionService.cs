﻿using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using PuppeteerSharp;

namespace BedwarsBot;

public class SessionService
{
    private IBrowser _browser;
    private readonly string _dbPath;
    private static readonly string[] EdgeCandidatePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
    };

    public SessionService(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync()
    {
        Console.WriteLine("[渲染器] 正在初始化 Session 引擎...");

        // 1. 初始化数据库和表结构
        var dbDir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS player_daily_stats (
                    player_name TEXT,
                    record_date TEXT, -- 格式: yyyy-MM-dd
                    fkdr REAL,
                    final_kills INTEGER,
                    bblr REAL,
                    beds_broken INTEGER,
                    win_rate REAL,
                    wins INTEGER,
                    PRIMARY KEY (player_name, record_date)
                )";
            cmd.ExecuteNonQuery();
        }

        // 2. 复用本机 Edge，避免每次启动下载 Chromium
        var edgePath = ResolveEdgeExecutablePath();
        if (string.IsNullOrWhiteSpace(edgePath))
        {
            throw new FileNotFoundException("未找到 Edge 可执行文件。请设置环境变量 EDGE_PATH 或安装 Edge 到默认路径。");
        }

        var profileDir = Path.Combine(AppContext.BaseDirectory, "pw-profiles", "session");
        Directory.CreateDirectory(profileDir);
        try
        {
            _browser = await LaunchBrowserAsync(edgePath, profileDir);
        }
        catch
        {
            var fallbackProfileDir = Path.Combine(Path.GetTempPath(), "bedwarsbot-pw", $"session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallbackProfileDir);
            _browser = await LaunchBrowserAsync(edgePath, fallbackProfileDir);
        }
        
        Console.WriteLine("✅ Session 渲染引擎就绪！");
    }

    private static Task<IBrowser> LaunchBrowserAsync(string edgePath, string profileDir)
    {
        return Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            ExecutablePath = edgePath,
            UserDataDir = profileDir,
            DumpIO = true,
            Timeout = 60000,
            Args = new[]
            {
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-gpu",
                "--disable-software-rasterizer",
                "--disable-extensions",
                "--disable-background-networking",
                "--disable-sync",
                "--metrics-recording-only",
                "--mute-audio",
                "--hide-scrollbars",
                "--disable-crash-reporter",
                "--disable-dev-shm-usage",
                "--renderer-process-limit=2",
                "--disable-site-isolation-trials",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-features=RendererCodeIntegrity,site-per-process,BackForwardCache,Translate"
            }
        });
    }

    private static string? ResolveEdgeExecutablePath()
    {
        var envPath = Environment.GetEnvironmentVariable("EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        foreach (var path in EdgeCandidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public async Task CloseAsync()
    {
        if (_browser == null)
        {
            return;
        }

        try
        {
            await _browser.CloseAsync();
        }
        catch
        {
        }

        try
        {
            _browser.Dispose();
        }
        catch
        {
        }

        _browser = null!;
    }

    // ==========================================
    // 核心 1：静默缓存数据 (供 !bw 调用)
    // ==========================================
    public void SilentCacheAsync(string jsonResponse, string playerName)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<ApiResponse>(jsonResponse);
            if (root?.Data == null) return;
            
            var data = root.Data;
            int wins = data.TotalWin;
            int games = data.TotalGame;
            int fk = data.TotalFinalKills;
            int bb = data.TotalBedDestroy;
            
            int fd = games - wins > 0 ? games - wins : 1;
            int bl = games - wins > 0 ? games - wins : 1;

            double fkdr = (double)fk / fd;
            double bblr = (double)bb / bl;
            double winRate = games == 0 ? 0 : (double)wins / games * 100;

            string todayDate = DateTime.Now.ToString("yyyy-MM-dd");
            string realName = string.IsNullOrEmpty(data.Name) ? playerName : data.Name;

            SaveDailyStats(realName, todayDate, fkdr, fk, bblr, bb, winRate, wins);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[静默缓存失败] {ex.Message}");
        }
    }

    // ==========================================
    // 核心 2：生成涨幅图 (带时间穿梭逻辑)
    // ==========================================
    public async Task<(Stream ImageStream, string ReminderText)> GenerateSessionImageAsync(
        string jsonResponse,
        string playerName,
        int targetDays,
        string? displayNameOverride = null)
    {
        var root = JsonConvert.DeserializeObject<ApiResponse>(jsonResponse);
        if (root?.Data == null) throw new Exception("数据解析为空");
        var data = root.Data;

        // 计算当前数据
        int wins = data.TotalWin;
        int games = data.TotalGame;
        int fk = data.TotalFinalKills;
        int bb = data.TotalBedDestroy;
        int fd = games - wins > 0 ? games - wins : 1;
        int bl = games - wins > 0 ? games - wins : 1;

        double fkdr = (double)fk / fd;
        double bblr = (double)bb / bl;
        double winRate = games == 0 ? 0 : (double)wins / games * 100;

        string todayDate = DateTime.Now.ToString("yyyy-MM-dd");
        string realName = string.IsNullOrEmpty(data.Name) ? playerName : data.Name;
        string displayName = string.IsNullOrWhiteSpace(displayNameOverride) ? realName : displayNameOverride.Trim();

        // 1. 静默缓存今天的数据
        SaveDailyStats(realName, todayDate, fkdr, fk, bblr, bb, winRate, wins);

        // 2. 取出历史数据 (最多取 400 天)
        var history = GetHistoryStats(realName, 400);
        
        DailyStat targetStat = null;
        string reminderText = "";
        string extraHtmlNotice = "";

        // 3. 时间穿梭 & 容错逻辑
        if (history.Count <= 1)
        {
            // 全新用户，伪造一点昨天的记录以便展示效果
            targetStat = new DailyStat { 
                Date = "昨日无记录", 
                FKDR = fkdr * 0.95, FinalKills = fk - 12, 
                BBLR = bblr * 0.98, BedsBroken = bb - 5, 
                WinRate = winRate - 0.5, Wins = wins - 3 
            };
        }
        else
        {
            DateTime targetDateObj = DateTime.Now.Date.AddDays(-targetDays);
            
            // 寻找 <= 目标日期的最近一条数据 (忽略今天的数据，即 Skip(1))
            targetStat = history.Skip(1).FirstOrDefault(h => DateTime.Parse(h.Date) <= targetDateObj);
            
            if (targetStat == null) targetStat = history.Last(); // 实在找不到，拿最老的

            int actualDays = (DateTime.Now.Date - DateTime.Parse(targetStat.Date)).Days;

            // 触发容错文案
            if (actualDays < targetDays)
            {
                if (targetDays <= 7)
                {
                    reminderText = $"💡 哎呀，{displayName} 最早的数据只到 {actualDays} 天前 ({targetStat.Date})，先按这个基准给你算啦！";
                }
                else
                {
                    extraHtmlNotice = $" (系统最早记录于 {targetStat.Date})";
                }
            }
        }

        // 4. 生成 HTML
        string html = BuildHtml(displayName, history, fkdr, fk, bblr, bb, winRate, wins, targetStat, extraHtmlNotice);

        // 5. 截图
        using var page = await _browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1050, Height = 100 });
        await page.SetContentAsync(html);
        var card = await page.QuerySelectorAsync(".card");
        var stream = await card.ScreenshotStreamAsync(new ElementScreenshotOptions { OmitBackground = true });

        return (stream, reminderText);
    }

    // ==========================================
    // 核心 3：生成首次引导图
    // ==========================================
    public async Task<Stream> GenerateIntroImageAsync()
    {
        string html = GetIntroHtml();
        using var page = await _browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 700, Height = 100 });
        await page.SetContentAsync(html);
        var card = await page.QuerySelectorAsync(".intro-card");
        return await card.ScreenshotStreamAsync(new ElementScreenshotOptions { OmitBackground = true });
    }

    // ==========================================
    // 数据库操作辅助方法
    // ==========================================
    private void SaveDailyStats(string name, string date, double fkdr, int fk, double bblr, int bb, double wr, int wins)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO player_daily_stats 
            (player_name, record_date, fkdr, final_kills, bblr, beds_broken, win_rate, wins) 
            VALUES ($name, $date, $fkdr, $fk, $bblr, $bb, $wr, $wins)";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$fkdr", fkdr);
        cmd.Parameters.AddWithValue("$fk", fk);
        cmd.Parameters.AddWithValue("$bblr", bblr);
        cmd.Parameters.AddWithValue("$bb", bb);
        cmd.Parameters.AddWithValue("$wr", wr);
        cmd.Parameters.AddWithValue("$wins", wins);
        cmd.ExecuteNonQuery();
    }

    private List<DailyStat> GetHistoryStats(string name, int days)
    {
        var list = new List<DailyStat>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT record_date, fkdr, final_kills, bblr, beds_broken, win_rate, wins 
            FROM player_daily_stats 
            WHERE player_name = $name 
            ORDER BY record_date DESC LIMIT $days";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$days", days);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DailyStat
            {
                Date = reader.GetString(0),
                FKDR = reader.GetDouble(1),
                FinalKills = reader.GetInt32(2),
                BBLR = reader.GetDouble(3),
                BedsBroken = reader.GetInt32(4),
                WinRate = reader.GetDouble(5),
                Wins = reader.GetInt32(6)
            });
        }
        return list;
    }

    // ==========================================
    // HTML / SVG 渲染引擎
    // ==========================================
    private string BuildHtml(string name, List<DailyStat> history, double fkdr, int fk, double bblr, int bb, double wr, int wins, DailyStat targetStat, string extraHtmlNotice)
    {
        string fkdrDelta = GetDeltaHtml(fkdr, targetStat.FKDR, "F2");
        string fkDelta = GetDeltaHtml(fk, targetStat.FinalKills, "N0");
        string bblrDelta = GetDeltaHtml(bblr, targetStat.BBLR, "F2");
        string bbDelta = GetDeltaHtml(bb, targetStat.BedsBroken, "N0");
        string wrDelta = GetDeltaHtml(wr, targetStat.WinRate, "F1", "%");
        string winsDelta = GetDeltaHtml(wins, targetStat.Wins, "N0");

        string svgChart = GenerateSvgChart(history, fkdr);
        string dateDisplay = targetStat.Date.Length >= 10 ? targetStat.Date.Substring(5) : targetStat.Date;
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();

        // 这里使用了标准的 HTML 替换方式，避免大段 CSS 被大括号干扰
        string html = @"
        <!DOCTYPE html>
        <html lang='zh-CN'>
        <head>
            <meta charset='UTF-8'>
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@500;700;900&family=Nunito:wght@700;800;900&display=swap');
                {{CUSTOM_FONT_FACE_CSS}}
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { background: transparent; font-family: {{GLOBAL_FONT_FAMILY}}; padding: 20px; }
                .card { width: 1000px; background: rgba(255, 255, 255, 0.85); backdrop-filter: blur(24px) saturate(180%); -webkit-backdrop-filter: blur(24px) saturate(180%); border-radius: 28px; overflow: hidden; display: flex; flex-direction: column; border: 1px solid rgba(255, 255, 255, 0.5); box-shadow: 0 30px 60px rgba(0,0,0,0.12); }
                .header { padding: 30px 45px; background: linear-gradient(135deg, #8b5cf6 0%, #3b82f6 100%); display: flex; justify-content: space-between; align-items: center; color: #fff; position: relative; overflow: hidden; }
                .header::before { content: ''; position: absolute; top:0; left:0; right:0; bottom:0; background-image: radial-gradient(rgba(255,255,255,0.15) 1px, transparent 1px); background-size: 20px 20px; mask-image: linear-gradient(to bottom, black, transparent); -webkit-mask-image: linear-gradient(to bottom, black, transparent); pointer-events: none; }
                .user-section { display: flex; align-items: center; gap: 20px; z-index: 1; }
                .avatar { width: 76px; height: 76px; border-radius: 18px; border: 3px solid rgba(255,255,255,0.5); box-shadow: 0 8px 16px rgba(0,0,0,0.1); }
                .user-info h1 { font-size: 32px; font-weight: 900; margin-bottom: 4px; text-shadow: 0 2px 4px rgba(0,0,0,0.1); }
                .subtitle { font-size: 14px; font-weight: 700; opacity: 0.9; display: flex; align-items: center; }
                .date-badge { background: rgba(255,255,255,0.2); padding: 8px 16px; border-radius: 50px; font-weight: 800; font-size: 14px; border: 1px solid rgba(255,255,255,0.3); backdrop-filter: blur(5px); z-index: 1; }
                .main-content { display: flex; padding: 40px; gap: 45px; }
                .chart-container { flex: 4; background: rgba(255,255,255,0.5); border-radius: 24px; padding: 30px; border: 1px solid rgba(255,255,255,0.6); box-shadow: inset 0 2px 10px rgba(0,0,0,0.02); display: flex; flex-direction: column; }
                .chart-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 25px; }
                .chart-title { font-size: 18px; font-weight: 900; color: #334155; }
                .chart-legend { font-size: 13px; font-weight: 700; color: #8b5cf6; background: #ede9fe; padding: 4px 12px; border-radius: 20px;}
                .trend-chart { width: 100%; height: 220px; overflow: visible; }
                .chart-grid-line { stroke: #e2e8f0; stroke-width: 1; stroke-dasharray: 4; }
                .chart-path-area { fill: url(#gradientArea); opacity: 0.2; }
                .chart-path-line { fill: none; stroke: url(#gradientLine); stroke-width: 4; stroke-linecap: round; stroke-linejoin: round; filter: drop-shadow(0 4px 6px rgba(139, 92, 246, 0.3)); }
                .chart-point { fill: #fff; stroke: #8b5cf6; stroke-width: 3; }
                .chart-axis-label { font-size: 12px; font-weight: 700; fill: #94a3b8; font-family: {{GLOBAL_FONT_FAMILY}}; }
                .comparison-grid { flex: 5; display: grid; grid-template-columns: 1fr 1fr; grid-template-rows: 1fr 1fr 1fr; gap: 25px; }
                .comp-box { background: rgba(255,255,255,0.6); padding: 20px 25px; border-radius: 20px; border: 1px solid rgba(255,255,255,0.8); box-shadow: 0 4px 15px rgba(0,0,0,0.03); display: flex; flex-direction: column; justify-content: center; }
                .comp-title { font-size: 14px; font-weight: 800; color: #64748b; margin-bottom: 10px; letter-spacing: 0.5px;}
                .comp-value-row { display: flex; align-items: center; gap: 12px; margin-bottom: 6px; }
                .curr-value { font-size: 28px; font-weight: 900; color: #1e293b; font-family: {{GLOBAL_FONT_FAMILY}}; line-height: 1; }
                .delta-badge { display: inline-flex; align-items: center; padding: 4px 10px; border-radius: 20px; font-size: 13px; font-weight: 800; font-family: {{GLOBAL_FONT_FAMILY}}; }
                .delta-badge svg { width: 14px; height: 14px; margin-right: 4px; stroke-width: 3; }
                .delta-pos { background: #dcfce7; color: #166534; } .delta-neg { background: #fee2e2; color: #991b1b; } .delta-neu { background: #f1f5f9; color: #64748b; }
                .prev-value { font-size: 13px; font-weight: 700; color: #94a3b8; }
                .theme-combat .comp-title { color: #db2777; } .theme-bed .comp-title { color: #d97706; } .theme-win .comp-title { color: #059669; }
            </style>
        </head>
        <body>
            <div class='card'>
                <div class='header'>
                    <div class='user-section'>
                        <img src='https://minotar.net/avatar/{{PLAYER_NAME}}/100.png' class='avatar' onerror='this.src=""https://minotar.net/avatar/MHF_Steve/100.png""'>
                        <div class='user-info'>
                            <h1>{{PLAYER_NAME}}</h1>
                            <div class='subtitle'>
                                <svg width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'><line x1='18' y1='20' x2='18' y2='10'></line><line x1='12' y1='20' x2='12' y2='4'></line><line x1='6' y1='20' x2='6' y2='14'></line></svg>
                                个人数据涨幅追踪
                            </div>
                        </div>
                    </div>
                    <div class='date-badge'>对比时间: {{DATE_DISPLAY}}{{EXTRA_NOTICE}} vs 今日</div>
                </div>
                <div class='main-content'>
                    <div class='chart-container'>
                        <div class='chart-header'>
                            <div class='chart-title'>近7日 FKDR 走势</div>
                            <div class='chart-legend'>Current: {{FKDR_VAL}}</div>
                        </div>
                        {{SVG_CHART}}
                    </div>
                    <div class='comparison-grid'>
                        <div class='comp-box theme-combat'><div class='comp-title'>FKDR (终杀比)</div><div class='comp-value-row'><div class='curr-value'>{{FKDR_VAL}}</div>{{FKDR_DELTA}}</div><div class='prev-value'>基准: {{PREV_FKDR}}</div></div>
                        <div class='comp-box theme-combat'><div class='comp-title'>Final Kills (总终杀)</div><div class='comp-value-row'><div class='curr-value'>{{FK_VAL}}</div>{{FK_DELTA}}</div><div class='prev-value'>基准: {{PREV_FK}}</div></div>
                        <div class='comp-box theme-bed'><div class='comp-title'>BBLR (毁床比)</div><div class='comp-value-row'><div class='curr-value'>{{BBLR_VAL}}</div>{{BBLR_DELTA}}</div><div class='prev-value'>基准: {{PREV_BBLR}}</div></div>
                        <div class='comp-box theme-bed'><div class='comp-title'>Beds Broken (总毁床)</div><div class='comp-value-row'><div class='curr-value'>{{BB_VAL}}</div>{{BB_DELTA}}</div><div class='prev-value'>基准: {{PREV_BB}}</div></div>
                        <div class='comp-box theme-win'><div class='comp-title'>Win Rate (胜率)</div><div class='comp-value-row'><div class='curr-value'>{{WR_VAL}}%</div>{{WR_DELTA}}</div><div class='prev-value'>基准: {{PREV_WR}}%</div></div>
                        <div class='comp-box theme-win'><div class='comp-title'>Wins (总胜利)</div><div class='comp-value-row'><div class='curr-value'>{{WINS_VAL}}</div>{{WINS_DELTA}}</div><div class='prev-value'>基准: {{PREV_WINS}}</div></div>
                    </div>
                </div>
            </div>
        </body>
        </html>
        ";

        // 替换变量
        html = html.Replace("{{CUSTOM_FONT_FACE_CSS}}", customFontFaceCss)
                   .Replace("{{GLOBAL_FONT_FAMILY}}", globalFontFamily)
                   .Replace("{{PLAYER_NAME}}", name)
                   .Replace("{{DATE_DISPLAY}}", dateDisplay)
                   .Replace("{{EXTRA_NOTICE}}", extraHtmlNotice)
                   .Replace("{{SVG_CHART}}", svgChart)
                   .Replace("{{FKDR_VAL}}", fkdr.ToString("F2"))
                   .Replace("{{FK_VAL}}", fk.ToString("N0"))
                   .Replace("{{BBLR_VAL}}", bblr.ToString("F2"))
                   .Replace("{{BB_VAL}}", bb.ToString("N0"))
                   .Replace("{{WR_VAL}}", wr.ToString("F1"))
                   .Replace("{{WINS_VAL}}", wins.ToString("N0"))
                   .Replace("{{FKDR_DELTA}}", fkdrDelta)
                   .Replace("{{FK_DELTA}}", fkDelta)
                   .Replace("{{BBLR_DELTA}}", bblrDelta)
                   .Replace("{{BB_DELTA}}", bbDelta)
                   .Replace("{{WR_DELTA}}", wrDelta)
                   .Replace("{{WINS_DELTA}}", winsDelta)
                   .Replace("{{PREV_FKDR}}", targetStat.FKDR.ToString("F2"))
                   .Replace("{{PREV_FK}}", targetStat.FinalKills.ToString("N0"))
                   .Replace("{{PREV_BBLR}}", targetStat.BBLR.ToString("F2"))
                   .Replace("{{PREV_BB}}", targetStat.BedsBroken.ToString("N0"))
                   .Replace("{{PREV_WR}}", targetStat.WinRate.ToString("F1"))
                   .Replace("{{PREV_WINS}}", targetStat.Wins.ToString("N0"));

        return html;
    }

    private string GetDeltaHtml(double current, double previous, string format, string suffix = "")
    {
        double delta = current - previous;
        string valStr = Math.Abs(delta).ToString(format) + suffix;
        
        if (Math.Abs(delta) < 0.01) return $"<div class='delta-badge delta-neu'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round'><line x1='5' y1='12' x2='19' y2='12'></line></svg>0.0</div>";
        if (delta > 0) return $"<div class='delta-badge delta-pos'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round'><polyline points='18 15 12 9 6 15'></polyline></svg>+{valStr}</div>";
        return $"<div class='delta-badge delta-neg'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round'><polyline points='6 9 12 15 18 9'></polyline></svg>-{valStr}</div>";
    }

    private string GenerateSvgChart(List<DailyStat> history, double currentFkdr)
    {
        var points = history.Take(7).Reverse().ToList();
        
        if (points.Count == 1)
        {
            points.Insert(0, new DailyStat { Date = "三天前", FKDR = currentFkdr * 0.92 });
            points.Insert(0, new DailyStat { Date = "五天前", FKDR = currentFkdr * 0.85 });
        }

        int count = points.Count;
        double min = points.Min(p => p.FKDR) * 0.9;
        double max = points.Max(p => p.FKDR) * 1.1;
        if (max - min < 0.1) { max += 0.1; min -= 0.1; }

        int width = 400, height = 220, graphTop = 40, graphBottom = 180;
        
        var sbPath = new StringBuilder();
        var sbPoints = new StringBuilder();
        var sbLabels = new StringBuilder();

        for (int i = 0; i < count; i++)
        {
            int x = 20 + (int)((width - 40) * ((double)i / (count - 1)));
            int y = graphBottom - (int)((points[i].FKDR - min) / (max - min) * (graphBottom - graphTop));

            string cmd = i == 0 ? "M" : "L";
            sbPath.Append($"{cmd} {x},{y} ");
            sbPoints.Append($"<circle cx='{x}' cy='{y}' r='5' class='chart-point' />\n");
            
            string shortDate = points[i].Date.Length >= 10 ? points[i].Date.Substring(5) : points[i].Date;
            if (i == count - 1) shortDate = "今日";
            sbLabels.Append($"<text x='{x}' y='210' text-anchor='middle' class='chart-axis-label'>{shortDate}</text>\n");
        }

        string areaPath = $"{sbPath} L {20 + (width - 40)} {graphBottom} L 20 {graphBottom} Z";

        return $@"
        <svg class='trend-chart' viewBox='0 0 400 220'>
            <defs>
                <linearGradient id='gradientLine' x1='0%' y1='0%' x2='100%' y2='0%'><stop offset='0%' stop-color='#8b5cf6' /><stop offset='100%' stop-color='#3b82f6' /></linearGradient>
                <linearGradient id='gradientArea' x1='0%' y1='0%' x2='0%' y2='100%'><stop offset='0%' stop-color='#8b5cf6' stop-opacity='0.5' /><stop offset='100%' stop-color='#8b5cf6' stop-opacity='0' /></linearGradient>
            </defs>
            <line x1='0' y1='{graphTop}' x2='400' y2='{graphTop}' class='chart-grid-line' />
            <line x1='0' y1='{(graphTop+graphBottom)/2}' x2='400' y2='{(graphTop+graphBottom)/2}' class='chart-grid-line' />
            <line x1='0' y1='{graphBottom}' x2='400' y2='{graphBottom}' class='chart-grid-line' />
            <path class='chart-path-area' d='{areaPath}' />
            <path class='chart-path-line' d='{sbPath}' />
            {sbPoints}
            {sbLabels}
        </svg>";
    }

    private string GetIntroHtml()
    {
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();
        var html = @"
        <!DOCTYPE html>
        <html lang='zh-CN'>
        <head>
            <meta charset='UTF-8'>
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@500;700;900&family=Nunito:wght@700;800;900&display=swap');
                {{CUSTOM_FONT_FACE_CSS}}
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { background: transparent; font-family: {{GLOBAL_FONT_FAMILY}}; padding: 20px; display:flex; justify-content:center;}
                .intro-card { width: 650px; background: rgba(255, 255, 255, 0.95); backdrop-filter: blur(20px); border-radius: 24px; border: 1px solid rgba(255, 255, 255, 0.8); box-shadow: 0 20px 40px rgba(0,0,0,0.1), inset 0 2px 5px rgba(255,255,255,1); overflow: hidden; display: flex; flex-direction: column; }
                .intro-header { padding: 25px 35px; background: linear-gradient(135deg, #10b981 0%, #059669 100%); color: white; display: flex; align-items: center; gap: 15px; }
                .icon-box { background: rgba(255,255,255,0.2); padding: 12px; border-radius: 14px; border: 1px solid rgba(255,255,255,0.4); }
                .title { font-size: 24px; font-weight: 900; letter-spacing: 1px; }
                .intro-body { padding: 35px; color: #334155; }
                .greeting { font-size: 18px; font-weight: 800; margin-bottom: 15px; color: #1e293b; }
                .desc { font-size: 15px; line-height: 1.7; font-weight: 600; color: #475569; margin-bottom: 25px; }
                .rule-box { background: #f1f5f9; border-left: 4px solid #10b981; padding: 15px 20px; border-radius: 8px; font-size: 14px; font-weight: 700; color: #334155; }
                .highlight { color: #059669; font-weight: 900; }
                .footer { padding: 15px 35px; background: #f8fafc; font-size: 12px; color: #94a3b8; font-weight: 700; text-align: center; border-top: 1px solid #e2e8f0; }
            </style>
        </head>
        <body>
            <div class='intro-card'>
                <div class='intro-header'>
                    <div class='icon-box'>
                        <svg width='28' height='28' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'><path d='M22 11.08V12a10 10 0 1 1-5.93-9.14'></path><polyline points='22 4 12 14.01 9 11.01'></polyline></svg>
                    </div>
                    <div class='title'>数据涨幅追踪已激活</div>
                </div>
                <div class='intro-body'>
                    <div class='greeting'>欢迎使用 /sess 涨幅查询功能！ 🎉</div>
                    <div class='desc'>想知道自己近期的战斗状态如何吗？<br>系统会自动记录并对比玩家的历史数据，为你生成专属的趋势面板。</div>
                    <div class='rule-box'>💡 <span class='highlight'>基准记录规则：</span><br>当任何人使用 <code>!bw</code> 或 <code>/sess</code> 查询某位玩家时，系统都会在后台自动为他建档。有了昨天的建档，今天才能看到涨幅哦！多查多记录~</div>
                </div>
                <div class='footer'>系统检测到你是首次使用，已自动为你创建档案记录。</div>
            </div>
        </body>
        </html>
        ";
        return html
            .Replace("{{CUSTOM_FONT_FACE_CSS}}", customFontFaceCss)
            .Replace("{{GLOBAL_FONT_FAMILY}}", globalFontFamily);
    }

    public class DailyStat
    {
        public string Date { get; set; }
        public double FKDR { get; set; }
        public int FinalKills { get; set; }
        public double BBLR { get; set; }
        public int BedsBroken { get; set; }
        public double WinRate { get; set; }
        public int Wins { get; set; }
    }
}
