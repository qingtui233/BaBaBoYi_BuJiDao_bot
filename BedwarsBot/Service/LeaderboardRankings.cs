using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PuppeteerSharp;

namespace BedwarsBot;

public class LeaderboardRankings
{
    private const string DefaultOfficialAvatarUuid = "fd99c31a-021f-464c-8773-c476878abac9";
    private static readonly Regex YearRegex = new(@"20\d{2}", RegexOptions.Compiled);
    private IBrowser _browser;
    private static readonly string[] EdgeCandidatePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
    };

    public async Task InitializeAsync()
    {
        Console.WriteLine("正在初始化排行榜渲染器...");

        var edgePath = ResolveEdgeExecutablePath();
        if (string.IsNullOrWhiteSpace(edgePath))
        {
            throw new FileNotFoundException("未找到 Edge 可执行文件。请设置环境变量 EDGE_PATH 或安装 Edge 到默认路径。");
        }

        var profileDir = Path.Combine(AppContext.BaseDirectory, "pw-profiles", "lb");
        Directory.CreateDirectory(profileDir);
        try
        {
            _browser = await LaunchBrowserAsync(edgePath, profileDir);
        }
        catch
        {
            var fallbackProfileDir = Path.Combine(Path.GetTempPath(), "bedwarsbot-pw", $"lb-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallbackProfileDir);
            _browser = await LaunchBrowserAsync(edgePath, fallbackProfileDir);
        }
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

    public async Task<Stream> GenerateLeaderboardImageAsync(
        string jsonResponse,
        string queriedPlayerName,
        string? customAvatarSrc = null,
        string? fallbackUuid = null)
    {
        var root = JObject.Parse(jsonResponse);
        var data = root["data"] ?? root;

        var displayName = ResolvePlayerName(data, queriedPlayerName);
        var isBanned = ReadBoolean(data, "banned", "is_banned", "isBanned");
        var rankings = ExtractRankings(root, data);

        var avatarSrc = ResolveAvatarSrc(customAvatarSrc, fallbackUuid);
        var htmlContent = BuildHtml(displayName, avatarSrc, isBanned, rankings.Count, rankings);

        var rowCount = Math.Max(2, rankings.Count);
        var height = Math.Clamp(360 + rowCount * 96, 780, 3800);

        using var page = await _browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 980, Height = height });
        await page.SetContentAsync(htmlContent);

        var cardElement = await page.QuerySelectorAsync(".board-card");
        if (cardElement == null)
        {
            throw new InvalidOperationException("排行榜卡片渲染失败");
        }

        return await cardElement.ScreenshotStreamAsync();
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

    private static string ResolvePlayerName(JToken data, string queriedPlayerName)
    {
        if (!string.IsNullOrWhiteSpace(queriedPlayerName))
        {
            return queriedPlayerName.Trim();
        }

        var name = FirstText(data, "playername", "username", "name");
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    private static bool ReadBoolean(JToken data, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = data[key];
            if (token == null) continue;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (bool.TryParse(token.ToString(), out var parsed)) return parsed;
            if (int.TryParse(token.ToString(), out var intValue)) return intValue != 0;
        }

        return false;
    }

    private static List<LeaderboardEntry> ExtractRankings(JToken root, JToken data)
    {
        var arrayCandidates = new List<JToken?>
        {
            data["result"],
            data["leaderboard"],
            data["leaderboards"],
            data["rankings"],
            data["ranks"],
            root["result"],
            root["leaderboard"],
            root["leaderboards"],
            root["rankings"],
            root["ranks"]
        };

        var sourceArray = arrayCandidates.FirstOrDefault(t => t is JArray);
        var result = new List<LeaderboardEntry>();
        if (sourceArray is not JArray array)
        {
            return result;
        }

        foreach (var item in array.OfType<JObject>())
        {
            var rank = ParseRank(item);
            if (rank <= 0) continue;

            var type = FirstText(item, "type") ?? string.Empty;
            var cnType = FirstText(item, "cn_type", "title", "name", "rank_type", "leaderboard_name", "display_name");
            var title = LocalizeRankTitle(type, cnType);
            var platform = DetectPlatform(type, cnType);
            var score = ParseScore(item);
            var cssClass = rank switch
            {
                1 => "top-1",
                2 => "top-2",
                3 => "top-3",
                _ => string.Empty
            };

            result.Add(new LeaderboardEntry(rank, title, score, cssClass, platform, type));
        }

        return result
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
    }

    private static string LocalizeRankTitle(string type, string? cnType)
    {
        if (!string.IsNullOrWhiteSpace(cnType) && HasChinese(cnType))
        {
            return CleanupCnType(cnType);
        }

        var raw = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(raw)) return "未命名排行";

        if (raw.StartsWith("bwxp_java") || raw.StartsWith("bwxp_pe"))
        {
            return "起床经验值";
        }

        var mode = raw.Contains("bwxp32") ? "起床战争(经典4队4人)" : "起床战争(全部模式)";
        var metric = "数据";

        if (raw.Contains("final_kills") || raw.Contains("bw_fk")) metric = "最终击杀";
        else if (raw.Contains("bed_destory") || raw.Contains("bed_destroy")) metric = "拆床";
        else if (raw.Contains("_win")) metric = "胜利";

        var yearMatch = YearRegex.Match(raw);
        if (yearMatch.Success)
        {
            return $"{yearMatch.Value}年 {mode}{metric}";
        }

        if (raw.Contains("weekly"))
        {
            return $"{mode}{metric}周榜";
        }

        if (raw.Contains("_all_") || raw.EndsWith("_all_java", StringComparison.Ordinal) || raw.EndsWith("_all_pe", StringComparison.Ordinal))
        {
            return $"{mode}{metric}总榜";
        }

        return $"{mode}{metric}";
    }

    private static string CleanupCnType(string cnType)
    {
        var text = cnType;
        var removes = new[]
        {
            "[端游]", "[手游]", "(端游)", "(手游)",
            "端游", "手游", "_java", "_pe", "java", "pe"
        };

        foreach (var marker in removes)
        {
            text = text.Replace(marker, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        text = Regex.Replace(text, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? "未命名排行" : text;
    }

    private static bool HasChinese(string text)
    {
        return text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
    }

    private static PlatformType DetectPlatform(string type, string? cnType)
    {
        var typeLower = (type ?? string.Empty).ToLowerInvariant();
        var cn = cnType ?? string.Empty;

        if (typeLower.EndsWith("_java", StringComparison.Ordinal) || cn.Contains("端游", StringComparison.Ordinal))
        {
            return PlatformType.Java;
        }

        if (typeLower.EndsWith("_pe", StringComparison.Ordinal) || cn.Contains("手游", StringComparison.Ordinal))
        {
            return PlatformType.Mobile;
        }

        return PlatformType.Unknown;
    }

    private static int ParseRank(JObject item)
    {
        var token = item["top"] ?? item["rank"] ?? item["position"] ?? item["ranking"];
        if (token == null) return -1;

        if (token.Type == JTokenType.Integer) return token.Value<int>();

        var text = token.ToString();
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : -1;
    }

    private static long ParseScore(JObject item)
    {
        var token = item["score"] ?? item["value"] ?? item["count"] ?? item["data"] ?? item["num"];
        if (token == null) return 0;

        if (token.Type == JTokenType.Integer) return token.Value<long>();
        if (token.Type == JTokenType.Float) return (long)token.Value<double>();

        var text = token.ToString().Replace(",", string.Empty);
        return long.TryParse(text, out var parsed) ? parsed : 0;
    }

    private static string? FirstText(JToken token, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = token[key]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string ResolveAvatarSrc(string? customAvatarSrc, string? fallbackUuid)
    {
        if (!string.IsNullOrWhiteSpace(customAvatarSrc)) return customAvatarSrc;

        if (!string.IsNullOrWhiteSpace(fallbackUuid))
        {
            var compactFallbackUuid = fallbackUuid.Replace("-", string.Empty);
            return $"https://skins.mcstats.com/face/{compactFallbackUuid}";
        }

        var compactDefaultUuid = DefaultOfficialAvatarUuid.Replace("-", string.Empty);
        return $"https://skins.mcstats.com/face/{compactDefaultUuid}";
    }

    private static string BuildHtml(
        string playerName,
        string avatarSrc,
        bool isBanned,
        int rankCount,
        List<LeaderboardEntry> rankings)
    {
        var statusText = isBanned ? "🔴 状态：封禁中" : "🟢 状态：正常游玩";
        var rowsHtml = BuildRowsHtml(rankings);
        var queryTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();

        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
            <meta charset="UTF-8">
            <title>Bedwars Bot - 荣耀排行榜</title>
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@500;700;900&family=Nunito:wght@700;800;900&display=swap');
                {{customFontFaceCss}}
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body {
                    font-family: {{globalFontFamily}};
                    background: url('https://images.unsplash.com/photo-1534447677768-be436bb09401?q=80&w=2094&auto=format&fit=crop') no-repeat center center fixed;
                    background-size: cover;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    min-height: 100vh;
                    padding: 40px;
                }
                .board-card {
                    width: 850px;
                    background: rgba(255, 255, 255, 0.75);
                    backdrop-filter: blur(30px) saturate(180%);
                    -webkit-backdrop-filter: blur(30px) saturate(180%);
                    border-radius: 32px;
                    overflow: hidden;
                    box-shadow: 0 40px 80px rgba(0,0,0,0.15), inset 0 2px 8px rgba(255,255,255,1);
                    border: 1px solid rgba(255, 255, 255, 0.8);
                    display: flex;
                    flex-direction: column;
                }
                .header {
                    padding: 35px 45px;
                    background: linear-gradient(135deg, rgba(84, 242, 242, 0.92) 0%, rgba(56, 214, 214, 0.92) 100%);
                    color: white;
                    display: flex;
                    justify-content: space-between;
                    align-items: center;
                    position: relative;
                }
                .header::before {
                    content: '';
                    position: absolute;
                    top: 0;
                    left: 0;
                    right: 0;
                    bottom: 0;
                    background-image: radial-gradient(rgba(255,255,255,0.2) 1px, transparent 1px);
                    background-size: 20px 20px;
                    pointer-events: none;
                }
                .user-section { display: flex; align-items: center; gap: 20px; z-index: 1; }
                .avatar {
                    width: 76px;
                    height: 76px;
                    border-radius: 20px;
                    border: 3px solid rgba(255,255,255,0.6);
                    box-shadow: 0 8px 16px rgba(0,0,0,0.15);
                }
                .user-info h1 {
                    font-size: 30px;
                    font-weight: 900;
                    letter-spacing: 1px;
                    margin-bottom: 4px;
                    text-shadow: 0 2px 4px rgba(0,0,0,0.1);
                    max-width: 320px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .user-status {
                    display: inline-block;
                    padding: 4px 12px;
                    border-radius: 50px;
                    font-size: 13px;
                    font-weight: 800;
                    background: rgba(255,255,255,0.2);
                    border: 1px solid rgba(255,255,255,0.3);
                    backdrop-filter: blur(4px);
                }
                .title-badge { z-index: 1; text-align: right; }
                .title-main {
                    font-size: 26px;
                    font-weight: 900;
                    letter-spacing: 2px;
                    text-shadow: 0 2px 4px rgba(0,0,0,0.1);
                }
                .title-sub {
                    font-size: 13px;
                    font-weight: 700;
                    opacity: 0.9;
                    margin-top: 4px;
                    letter-spacing: 1px;
                }
                .list-container {
                    padding: 30px 42px;
                    display: flex;
                    flex-direction: column;
                    gap: 12px;
                    background: rgba(248, 250, 252, 0.4);
                }
                .rank-row {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    padding: 14px 20px;
                    border-radius: 18px;
                    background: rgba(255, 255, 255, 0.85);
                    border: 1px solid rgba(255, 255, 255, 0.9);
                    box-shadow: 0 4px 12px rgba(0,0,0,0.03);
                }
                .rank-row-muted {
                    background: rgba(255,255,255,0.40);
                    border-color: rgba(255,255,255,0.55);
                }
                .row-left { display: flex; align-items: center; gap: 16px; min-width: 0; }
                .rank-badge {
                    width: 82px;
                    height: 30px;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    border-radius: 10px;
                    font-size: 14px;
                    font-weight: 900;
                    font-family: {{globalFontFamily}};
                    letter-spacing: 0.5px;
                    color: white;
                    box-shadow: 0 4px 8px rgba(0,0,0,0.1);
                    flex-shrink: 0;
                }
                .rank-1 { background: linear-gradient(135deg, #fde047, #f59e0b); box-shadow: 0 4px 10px rgba(245, 158, 11, 0.3); }
                .rank-2 { background: linear-gradient(135deg, #f1f5f9, #94a3b8); box-shadow: 0 4px 10px rgba(148, 163, 184, 0.3); color: #1e293b; }
                .rank-3 { background: linear-gradient(135deg, #fed7aa, #ea580c); box-shadow: 0 4px 10px rgba(234, 88, 12, 0.3); }
                .rank-top10 { background: linear-gradient(135deg, #a78bfa, #7c3aed); box-shadow: 0 4px 10px rgba(124, 58, 237, 0.2); }
                .rank-top50 { background: linear-gradient(135deg, #f472b6, #db2777); box-shadow: 0 4px 10px rgba(219, 39, 119, 0.2); }
                .rank-top100 { background: linear-gradient(135deg, #7dd3fc, #0284c7); box-shadow: 0 4px 10px rgba(2, 132, 199, 0.2); }
                .rank-normal { background: #e2e8f0; color: #64748b; box-shadow: none; border: 1px solid #cbd5e1; }
                .cat-info { display: flex; align-items: center; gap: 10px; min-width: 0; }
                .cat-icon {
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    width: 32px;
                    height: 32px;
                    border-radius: 10px;
                    background: #f1f5f9;
                    color: #64748b;
                    flex-shrink: 0;
                }
                .cat-text { min-width: 0; }
                .cat-name {
                    font-size: 16px;
                    font-weight: 800;
                    color: #334155;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                .cat-mode {
                    font-size: 14px;
                    font-weight: 700;
                    color: #94a3b8;
                    margin-left: 6px;
                }
                .cat-sub {
                    font-size: 14px;
                    font-weight: 700;
                    color: #94a3b8;
                    margin-top: 2px;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                .row-right {
                    font-size: 24px;
                    font-weight: 900;
                    font-family: {{globalFontFamily}};
                    text-align: right;
                    background: -webkit-linear-gradient(45deg, #334155, #64748b);
                    -webkit-background-clip: text;
                    -webkit-text-fill-color: transparent;
                    margin-left: 12px;
                    flex-shrink: 0;
                }
                .row-right-muted {
                    color: #94a3b8;
                    -webkit-text-fill-color: #94a3b8;
                }
                .icon-kill { color: #ef4444; background: #fee2e2; }
                .icon-bed { color: #f59e0b; background: #fef3c7; }
                .icon-win { color: #10b981; background: #d1fae5; }
                .icon-exp { color: #3b82f6; background: #dbeafe; }
                .footer {
                    padding: 18px;
                    text-align: center;
                    background: rgba(255,255,255,0.7);
                    border-top: 1px solid rgba(226, 232, 240, 0.8);
                    font-size: 13px;
                    font-weight: 700;
                    color: #94a3b8;
                }
            </style>
        </head>
        <body>
            <div class="board-card">
                <div class="header">
                    <div class="user-section">
                        <img src="{{avatarSrc}}" class="avatar">
                        <div class="user-info">
                            <h1>{{WebUtility.HtmlEncode(playerName)}}</h1>
                            <div class="user-status">{{statusText}}</div>
                        </div>
                    </div>
                    <div class="title-badge">
                        <div class="title-main">LEADERBOARD</div>
                        <div class="title-sub">布吉岛 • 荣耀排行（{{rankCount}} 项）</div>
                    </div>
                </div>
                <div class="list-container">
                    {{rowsHtml}}
                </div>
                <div class="footer">
                    查询时间：{{queryTime}} &nbsp;|&nbsp; Generated by Soft UI Bedwars Bot
                </div>
            </div>
        </body>
        </html>
        """;
    }

    private static string BuildRowsHtml(List<LeaderboardEntry> rankings)
    {
        if (rankings.Count == 0)
        {
            return """
                <div class="rank-row rank-row-muted">
                    <div class="row-left">
                        <div class="rank-badge rank-normal">第 - 名</div>
                        <div class="cat-info">
                            <div class="cat-icon icon-exp">
                                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"></polygon></svg>
                            </div>
                            <div class="cat-text">
                                <div class="cat-name">暂无上榜数据</div>
                                <div class="cat-sub">起床战争</div>
                            </div>
                        </div>
                    </div>
                    <div class="row-right row-right-muted">0</div>
                </div>
            """;
        }

        return string.Join(Environment.NewLine, rankings.Select(BuildRowHtml));
    }

    private static string BuildRowHtml(LeaderboardEntry rank)
    {
        var rankClass = GetRankBadgeClass(rank.Rank);
        var rowClass = rankClass == "rank-normal" ? "rank-row rank-row-muted" : "rank-row";
        var scoreClass = rankClass == "rank-normal" ? "row-right row-right-muted" : "row-right";
        var icon = ResolveMetricIcon(rank.RawType, rank.Title);
        var metric = ResolveMetricName(rank.RawType, rank.Title);
        var period = ResolvePeriodLabel(rank.RawType);
        var mode = ResolveModeLabel(rank.RawType, rank.Title);

        return $"""
            <div class="{rowClass}">
                <div class="row-left">
                    <div class="rank-badge {rankClass}">第 {rank.Rank} 名</div>
                    <div class="cat-info">
                        <div class="cat-icon {icon.CssClass}">{icon.Svg}</div>
                        <div class="cat-text">
                            <div class="cat-name">{WebUtility.HtmlEncode(metric)} <span class="cat-mode">({WebUtility.HtmlEncode(period)})</span></div>
                            <div class="cat-sub">{WebUtility.HtmlEncode(mode)}</div>
                        </div>
                    </div>
                </div>
                <div class="{scoreClass}">{rank.Score:N0}</div>
            </div>
        """;
    }

    private static string GetRankBadgeClass(int rank)
    {
        if (rank == 1) return "rank-1";
        if (rank == 2) return "rank-2";
        if (rank == 3) return "rank-3";
        if (rank <= 10) return "rank-top10";
        if (rank <= 50) return "rank-top50";
        if (rank <= 100) return "rank-top100";
        return "rank-normal";
    }

    private static string ResolveMetricName(string rawType, string title)
    {
        var raw = rawType.ToLowerInvariant();
        var normalizedTitle = title ?? string.Empty;

        if (normalizedTitle.Contains("最终击杀", StringComparison.Ordinal) || ContainsAny(raw, "final_kills", "bw_fk"))
        {
            return "最终击杀";
        }

        if (normalizedTitle.Contains("拆床", StringComparison.Ordinal) || ContainsAny(raw, "bed_destory", "bed_destroy"))
        {
            return "拆床";
        }

        if (normalizedTitle.Contains("胜利", StringComparison.Ordinal) || ContainsAny(raw, "_win", "wins"))
        {
            return "胜利";
        }

        if (normalizedTitle.Contains("经验", StringComparison.Ordinal) || ContainsAny(raw, "bwxp_", "exp", "experience"))
        {
            return "经验值";
        }

        return "数据";
    }

    private static string ResolvePeriodLabel(string rawType)
    {
        var raw = rawType.ToLowerInvariant();
        var yearMatch = YearRegex.Match(raw);
        if (yearMatch.Success)
        {
            var year = yearMatch.Value;
            return year.Length == 4 ? $"{year[2..]}年榜" : $"{year}年榜";
        }

        if (ContainsAny(raw, "daily", "_day", "day_")) return "日榜";
        if (ContainsAny(raw, "weekly", "_week", "week_")) return "周榜";
        if (ContainsAny(raw, "monthly", "_month", "month_")) return "月榜";
        if (ContainsAny(raw, "_all_", "total", "_all_java", "_all_pe")) return "总榜";
        return "排行榜";
    }

    private static string ResolveModeLabel(string rawType, string title)
    {
        var raw = rawType.ToLowerInvariant();
        var titleText = title ?? string.Empty;

        if (titleText.Contains("单人", StringComparison.Ordinal) || ContainsAny(raw, "bw1", "solo", "1s"))
        {
            return "起床战争 · 单人";
        }

        if (titleText.Contains("双人", StringComparison.Ordinal) || ContainsAny(raw, "bw8", "2s", "duo", "double"))
        {
            return "起床战争 · 双人";
        }

        if (titleText.Contains("4队4人", StringComparison.Ordinal) || ContainsAny(raw, "bw16", "bwxp32", "4s", "44"))
        {
            return "起床战争 · 4队4人";
        }

        if (titleText.Contains("全部模式", StringComparison.Ordinal) || ContainsAny(raw, "_all_", "all_"))
        {
            return "起床战争 · 全部模式";
        }

        return "起床战争";
    }

    private static (string CssClass, string Svg) ResolveMetricIcon(string rawType, string title)
    {
        return ResolveMetricName(rawType, title) switch
        {
            "最终击杀" => ("icon-kill", "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M14.5 17.5L3 6V3h3l11.5 11.5\"></path><path d=\"M13 19l6-6\"></path><path d=\"M16 16l4 4\"></path><path d=\"M19 21l2-2\"></path></svg>"),
            "拆床" => ("icon-bed", "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M2 4v16\"></path><path d=\"M2 8h18a2 2 0 0 1 2 2v10\"></path><path d=\"M2 17h20\"></path><path d=\"M6 8v9\"></path></svg>"),
            "胜利" => ("icon-win", "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><circle cx=\"12\" cy=\"8\" r=\"6\"></circle><path d=\"M15.477 12.89L17 22l-5-3-5 3 1.523-9.11\"></path></svg>"),
            _ => ("icon-exp", "<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><polygon points=\"12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2\"></polygon></svg>")
        };
    }

    private static bool ContainsAny(string source, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct LeaderboardEntry(int Rank, string Title, long Score, string CssClass, PlatformType Platform, string RawType);

    private enum PlatformType
    {
        Unknown = 0,
        Java = 1,
        Mobile = 2
    }
}
