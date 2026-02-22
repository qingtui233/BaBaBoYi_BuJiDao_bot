using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using PuppeteerSharp;

namespace BedwarsBot;

public class ShoutLogService
{
    private const string DefaultOfficialAvatarUuid = "fd99c31a-021f-464c-8773-c476878abac9";
    private readonly string _dbPath;
    private readonly BotDataStore _store;
    private IBrowser _browser;
    private readonly Dictionary<string, string> _avatarSrcCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] EdgeCandidatePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
    };

    public ShoutLogService(string dbPath, BotDataStore store)
    {
        _dbPath = dbPath;
        _store = store;
    }

    public async Task InitializeAsync()
    {
        var edgePath = ResolveEdgeExecutablePath();
        if (string.IsNullOrWhiteSpace(edgePath))
        {
            throw new FileNotFoundException("未找到 Edge 可执行文件。请设置环境变量 EDGE_PATH 或安装 Edge 到默认路径。");
        }

        var profileDir = Path.Combine(AppContext.BaseDirectory, "pw-profiles", "shout");
        Directory.CreateDirectory(profileDir);
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            ExecutablePath = edgePath,
            UserDataDir = profileDir,
            DumpIO = true,
            Timeout = 60000,
            Args =
            [
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
            ]
        });
    }

    public async Task<Stream> GenerateShoutImageAsync(DateTime startTime, int durationMinutes = 30)
    {
        if (!File.Exists(_dbPath))
        {
            throw new InvalidOperationException("未找到喊话内容，可能是数据库不存在");
        }

        var endTime = startTime.AddMinutes(durationMinutes);
        long startSec = ((DateTimeOffset)startTime).ToUnixTimeSeconds();
        long endSec = ((DateTimeOffset)endTime).ToUnixTimeSeconds();
        long startMs = startSec * 1000;
        long endMs = endSec * 1000;

        var logs = new List<(string Name, string Content, string Time)>();

        try
        {
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"SELECT player, content, created_at
                      FROM shouts
                      WHERE (created_at >= $startSec AND created_at < $endSec)
                         OR (created_at >= $startMs AND created_at < $endMs)
                      ORDER BY created_at ASC";
                command.Parameters.AddWithValue("$startSec", startSec);
                command.Parameters.AddWithValue("$endSec", endSec);
                command.Parameters.AddWithValue("$startMs", startMs);
                command.Parameters.AddWithValue("$endMs", endMs);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var player = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
                    var content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var ts = reader.GetInt64(2);
                    if (ts > 10_000_000_000) ts /= 1000;

                    var timeText = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("HH:mm:ss");
                    logs.Add((player, content, timeText));
                }
            }
        }
        catch (SqliteException)
        {
            throw new InvalidOperationException("未找到喊话内容，可能是数据库不存在");
        }

        if (logs.Count == 0)
        {
            throw new InvalidOperationException("未找到喊话内容，可能是数据库不存在");
        }

        var html = GetHtmlTemplate(logs, startTime, endTime);

        using var page = await _browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 750, Height = 100 });
        await page.SetContentAsync(html);

        var body = await page.QuerySelectorAsync(".card");
        if (body == null) throw new Exception("喊话图片渲染失败");
        return await body.ScreenshotStreamAsync();
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

    private string GetChatRowsHtml(List<(string Name, string Content, string Time)> logs)
    {
        if (logs.Count == 0)
        {
            return """
            <div class='empty-state'>
                <div style='font-size:40px; color:#cbd5e1;'>∅</div>
                <div class='empty-text'>该时间段内没有检测到喊话</div>
            </div>
            """;
        }

        var sb = new StringBuilder();
        foreach (var log in logs)
        {
            var avatarSrc = ResolveAvatarSrc(log.Name);
            sb.Append($@"
            <div class='chat-row'>
                <img src='{avatarSrc}' class='avatar'>
                <div class='bubble'>
                    <div class='bubble-meta'>
                        <span class='user-name'>{WebUtility.HtmlEncode(log.Name)}</span>
                        <span class='msg-time'>{log.Time}</span>
                    </div>
                    <div class='msg-content'>{WebUtility.HtmlEncode(log.Content)}</div>
                </div>
            </div>");
        }

        return sb.ToString();
    }

    private string GetHtmlTemplate(List<(string Name, string Content, string Time)> logs, DateTime start, DateTime end)
    {
        var rowsHtml = GetChatRowsHtml(logs);
        var timeRange = $"{start:MM月dd日 HH:mm} - {end:HH:mm}";
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();

        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
            <meta charset="UTF-8">
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@400;500;700&family=Nunito:wght@600;700;800&display=swap');
                {{customFontFaceCss}}
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body { background: transparent; font-family: {{globalFontFamily}}; padding: 20px; }

                .card {
                    width: 700px;
                    background: #ffffff;
                    border-radius: 24px;
                    box-shadow: 0 10px 40px rgba(0, 0, 0, 0.08);
                    overflow: hidden;
                    border: 1px solid rgba(0,0,0,0.02);
                    padding-bottom: 20px;
                }

                .header {
                    padding: 25px 35px;
                    border-bottom: 2px solid #f1f5f9;
                    background: linear-gradient(to right, #ffffff, #f8fafc);
                    display: flex;
                    justify-content: space-between;
                    align-items: center;
                }

                .title-group { display: flex; align-items: center; gap: 12px; }
                .icon-box {
                    width: 42px; height: 42px; background: #eef2ff; color: #6366f1;
                    border-radius: 12px; display: flex; align-items: center; justify-content: center;
                }
                .title-text { font-size: 20px; font-weight: 800; color: #334155; }
                .time-badge { font-size: 14px; font-weight: 700; color: #64748b; background: #f1f5f9; padding: 6px 14px; border-radius: 50px; }

                .chat-list { padding: 25px 35px; display: flex; flex-direction: column; gap: 20px; }
                .chat-row { display: flex; align-items: flex-start; gap: 16px; }
                .avatar { width: 48px; height: 48px; border-radius: 12px; background: #eee; flex-shrink: 0; }

                .bubble {
                    background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 0 16px 16px 16px;
                    padding: 12px 18px; flex: 1;
                }
                .bubble-meta { display: flex; align-items: baseline; gap: 10px; margin-bottom: 6px; }
                .user-name { font-size: 15px; font-weight: 800; color: #475569; }
                .msg-time { font-size: 12px; color: #94a3b8; font-weight: 600; }
                .msg-content { font-size: 15px; color: #1e293b; line-height: 1.6; font-weight: 500; }

                .empty-state { padding: 50px; text-align: center; color: #94a3b8; }
                .empty-text { margin-top: 10px; font-weight: 600; font-size: 14px; }
                .footer-note { text-align: center; font-size: 12px; color: #cbd5e1; margin-top: 10px; font-weight: 600; }
            </style>
        </head>
        <body>
            <div class="card">
                <div class="header">
                    <div class="title-group">
                        <div class="icon-box">
                            <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5L6 9H2v6h4l5 4V5z"></path><path d="M19.07 4.93a10 10 0 0 1 0 14.14M15.54 8.46a5 5 0 0 1 0 7.07"></path></svg>
                        </div>
                        <div class="title-text">全服喊话记录</div>
                    </div>
                    <div class="time-badge">{{timeRange}}</div>
                </div>
                <div class="chat-list">
                    {{rowsHtml}}
                </div>
                <div class="footer-note">Generated by BedwarsBot • Data from SQLite</div>
            </div>
        </body>
        </html>
        """;
    }

    private string ResolveAvatarSrc(string playerName)
    {
        if (_avatarSrcCache.TryGetValue(playerName, out var cached))
        {
            return cached;
        }

        var defaultSrc = GetDefaultAvatarSrc();
        if (_store.TryGetQqBindingByPlayerName(playerName, out var binding) &&
            !string.IsNullOrWhiteSpace(binding.BjdUuid) &&
            _store.TryGetSkinBinding(binding.BjdUuid, out var skinBinding))
        {
            var avatarPath = Path.Combine(_store.AvatarDirectory, skinBinding.AvatarFileName);
            if (File.Exists(avatarPath))
            {
                var bytes = File.ReadAllBytes(avatarPath);
                if (bytes.Length > 0)
                {
                    var src = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                    _avatarSrcCache[playerName] = src;
                    return src;
                }
            }
        }

        _avatarSrcCache[playerName] = defaultSrc;
        return defaultSrc;
    }

    private static string GetDefaultAvatarSrc()
    {
        var compactUuid = DefaultOfficialAvatarUuid.Replace("-", string.Empty);
        return $"https://skins.mcstats.com/face/{compactUuid}";
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
}
