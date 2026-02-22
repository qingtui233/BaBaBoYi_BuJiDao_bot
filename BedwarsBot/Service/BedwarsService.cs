using System.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PuppeteerSharp;

namespace BedwarsBot;

public class BedwarsService
{
    private const string DefaultOfficialAvatarUuid = "fd99c31a-021f-464c-8773-c476878abac9";
    private const string IconsConfigDirectoryName = "pz";
    private const string IconsConfigFileName = "bedwars-icons.json";
    private const string HtmlTemplateDirectoryName = "HTML";
    private const string HtmlTemplateFileName = "bw-stats-template.html";
    private static readonly TimeSpan RenderedImageCacheTtl = TimeSpan.FromSeconds(60);
    private const int RenderedImageCacheMaxEntries = 220;
    private static readonly Regex GoogleFontImportRegex = new(
        @"@import\s+url\((['""])https://fonts\.googleapis\.com[^)]*\1\)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private IBrowser? _browser;
    private IPage? _renderPage;
    private readonly SemaphoreSlim _browserLifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly ConcurrentDictionary<string, RenderedImageCacheEntry> _renderedImageCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<byte[]>> _renderInFlight = new(StringComparer.Ordinal);
    private IconConfig? _iconConfig;
    private DateTime _iconConfigLastWriteUtc;
    private string? _htmlTemplateCache;
    private DateTime _htmlTemplateLastWriteUtc;
    private static readonly string[] EdgeCandidatePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
    };

    // 简单的汉化字典
    private readonly Dictionary<string, string> _cnDict = new()
    {
        {"fire_charge", "火焰弹"}, {"fireball", "火焰弹"},
        {"egg", "搭桥蛋"},
        {"blaze_rod", "救援平台"},
        {"golden_apple", "金苹果"}, {"gapple", "金苹果"},
        {"chest", "堡垒"}, {"tnt", "TNT"},
        {"snowball", "蠹虫"}, {"wolf_spawn_egg", "铁傀儡"},
        {"ender_pearl", "末影珍珠"},
        {"water_bucket", "水桶"}, {"wool", "羊毛"},
        {"glass", "防爆玻璃"}, {"wood", "木板"},
        {"defense", "防御陷阱"}, {"sharpness", "锋利"},
        {"protection", "保护"}, {"fast_dig", "疯狂矿工"},
        {"forge", "资源池"}, {"iron_forge", "铁溶炉"},
        {"heal", "治愈池"}, {"trap", "这是一个陷阱"},
        {"mining_fatigue", "挖掘疲劳"}, {"alarm_trap", "报警陷阱"},
        {"counterattack_trap", "反击陷阱"}, {"counter_offensive_trap", "反击陷阱"}
    };

    private static readonly string[] DefaultItemsOrder =
    {
        "fireball",
        "egg",
        "blaze_rod",
        "golden_apple",
        "tnt",
        "ender_pearl",
        "snowball",
        "glass",
        "chest",
        "wolf_spawn_egg"
    };

    private static readonly HashSet<string> HiddenItemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "wool",
        "water_bucket",
        "wood",
        "glass"
    };

    private static readonly HashSet<string> HiddenUpgradeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "forge",
        "counterattack_trap",
        "mining_fatigue"
    };

    private static readonly string[] DefaultUpgradesOrder =
    {
        "sharpness",
        "protection",
        "fast_dig",
        "iron_forge",
        "heal",
        "trap",
        "defense",
        "alarm_trap",
        "counter_offensive_trap"
    };

    private static readonly Dictionary<string, string> ItemKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        {"fire_charge", "fireball"},
        {"gapple", "golden_apple"},
        {"iron_golem_spawn_egg", "wolf_spawn_egg"},
        {"bridge_egg", "egg"},
        {"rescue_platform", "blaze_rod"}
    };

    private static readonly Dictionary<string, string> ModeAliasToKey = new(StringComparer.OrdinalIgnoreCase)
    {
        {"all", "all"},
        {"total", "all"},
        {"overall", "all"},
        {"总览", "all"},
        {"全部", "all"},
        {"solo", "bw1"},
        {"单八", "bw1"},
        {"1s", "bw1"},
        {"bw1", "bw1"},
        {"duo", "bw8"},
        {"double", "bw8"},
        {"doubles", "bw8"},
        {"2s", "bw8"},
        {"双八", "bw8"},
        {"bw16", "bw16"},
        {"squad", "bw16"},
        {"4s", "bw16"},
        {"44", "bw16"},
        {"46", "bw16"},
        {"64", "bw16"},
        {"bw8", "bw8"},
        {"xp", "bwxp32"},
        {"xp32", "bwxp32"},
        {"48", "bwxp32"},
        {"bwxp32", "bwxp32"},
        {"xp64", "bwxp64"},
        {"bwxp64", "bwxp64"},
        {"xp8x4", "bwxp8x4"},
        {"bwxp8x4", "bwxp8x4"},
        {"bw999", "bw999"}
    };

    private static readonly Dictionary<char, string> McColorMap = new()
    {
        ['0'] = "#000000",
        ['1'] = "#0000AA",
        ['2'] = "#00AA00",
        ['3'] = "#00AAAA",
        ['4'] = "#AA0000",
        ['5'] = "#AA00AA",
        ['6'] = "#FFAA00",
        ['7'] = "#AAAAAA",
        ['8'] = "#555555",
        ['9'] = "#5555FF",
        ['a'] = "#55FF55",
        ['b'] = "#55FFFF",
        ['c'] = "#FF5555",
        ['d'] = "#FF55FF",
        ['e'] = "#FFFF55",
        ['f'] = "#FFFFFF"
    };

    private sealed class IconConfig
    {
        public Dictionary<string, string>? Items { get; set; }
        public Dictionary<string, string>? Upgrades { get; set; }

        [JsonIgnore]
        public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

        [JsonIgnore]
        public Dictionary<string, string> ItemsNormalized { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public Dictionary<string, string> UpgradesNormalized { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        public void Normalize()
        {
            ItemsNormalized = NormalizeDictionary(Items, NormalizeItemKey);
            UpgradesNormalized = NormalizeDictionary(Upgrades, NormalizeUpgradeKey);
        }

        private static Dictionary<string, string> NormalizeDictionary(
            Dictionary<string, string>? source,
            Func<string, string> normalizer)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return result;

            foreach (var kvp in source)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                var normalizedKey = normalizer(kvp.Key);
                result[normalizedKey] = kvp.Value;
            }

            return result;
        }
    }

    private sealed class RenderedImageCacheEntry
    {
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    public async Task InitializeAsync()
    {
        Console.WriteLine("正在检查 Microsoft Edge 浏览器...");
        EnsureIconConfigFile();
        await EnsureBrowserReadyAsync(forceRestart: true);
        Console.WriteLine("Edge 初始化完成");
    }

    public async Task CloseAsync()
    {
        await _renderLock.WaitAsync();
        await _browserLifecycleLock.WaitAsync();
        try
        {
            await CloseRenderPageNoThrowAsync();
            if (_browser == null)
            {
                return;
            }

            await CloseBrowserNoThrowAsync(_browser);
            _browser = null;
            _renderedImageCache.Clear();
            _renderInFlight.Clear();
        }
        finally
        {
            _browserLifecycleLock.Release();
            _renderLock.Release();
        }
    }

    private async Task EnsureBrowserReadyAsync(bool forceRestart = false)
    {
        await _browserLifecycleLock.WaitAsync();
        try
        {
            if (!forceRestart && _browser != null && _browser.IsConnected)
            {
                return;
            }

            if (_browser != null)
            {
                await CloseRenderPageNoThrowAsync();
                await CloseBrowserNoThrowAsync(_browser);
                _browser = null;
            }

            var edgePath = ResolveEdgeExecutablePath();
            if (string.IsNullOrWhiteSpace(edgePath))
            {
                throw new FileNotFoundException(
                    "未找到 Edge 可执行文件。请设置环境变量 EDGE_PATH 或安装 Edge 到默认路径。");
            }

            var profileDir = Path.Combine(AppContext.BaseDirectory, "pw-profiles", "bw");
            Directory.CreateDirectory(profileDir);
            try
            {
                _browser = await LaunchBrowserAsync(edgePath, profileDir);
            }
            catch
            {
                var fallbackProfileDir = Path.Combine(Path.GetTempPath(), "bedwarsbot-pw", $"bw-{Guid.NewGuid():N}");
                Directory.CreateDirectory(fallbackProfileDir);
                _browser = await LaunchBrowserAsync(edgePath, fallbackProfileDir);
            }
        }
        finally
        {
            _browserLifecycleLock.Release();
        }
    }

    private async Task CloseRenderPageNoThrowAsync()
    {
        if (_renderPage == null)
        {
            return;
        }

        try
        {
            await _renderPage.CloseAsync();
        }
        catch
        {
        }

        try
        {
            _renderPage.Dispose();
        }
        catch
        {
        }

        _renderPage = null;
    }

    private static async Task CloseBrowserNoThrowAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync();
        }
        catch
        {
            // ignore shutdown errors
        }

        try
        {
            browser.Dispose();
        }
        catch
        {
            // ignore shutdown errors
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

    public async Task<Stream> GenerateStatsImageAsync(
        string jsonResponse,
        string? customAvatarSrc = null,
        string? fallbackUuid = null,
        string? queriedPlayerName = null,
        string? bwxpShow = null,
        string? modeToken = null,
        string? customBackgroundSrc = null,
        string? customSolidBackgroundColor = null,
        double backgroundOpacity = 0.6,
        int chipIconSize = 28,
        int playerIdFontSize = 14,
        string? customTitleBadgeHtml = null)
    {
        var root = JsonConvert.DeserializeObject<ApiResponse>(jsonResponse);
        if (root == null || root.Data == null) throw new Exception("数据解析为空");
        var data = root.Data;
        var baseDisplayName = ResolveDisplayName(data, queriedPlayerName);

        int totalWins = data.TotalWin;
        int totalFinalKills = data.TotalFinalKills;
        int totalBedsBroken = data.TotalBedDestroy;
        int totalGames = data.TotalGame;

        int totalLosses = totalGames - totalWins;
        int totalFinalDeaths = 0;
        int totalBedsLost = 0;
        int totalKills = 0;
        int totalDeaths = 0;

        var totalItems = new Dictionary<string, int>();
        var totalUpgrades = new Dictionary<string, int>();
        var modeDisplayName = "总览";

        if (!string.IsNullOrWhiteSpace(modeToken))
        {
            var modeKey = ResolveModeKey(data.BedwarsModes, modeToken);
            if (string.IsNullOrWhiteSpace(modeKey) || modeKey == "all")
            {
                modeDisplayName = "总览";
            }
            else
            {
                if (data.BedwarsModes == null || !data.BedwarsModes.TryGetValue(modeKey, out var mode))
                {
                    var modeList = data.BedwarsModes == null || data.BedwarsModes.Count == 0
                        ? "无"
                        : string.Join(", ", data.BedwarsModes.Keys.OrderBy(x => x));
                    throw new InvalidOperationException($"未找到模式 {modeToken}，可用模式: {modeList}");
                }

                modeDisplayName = GetModeDisplayName(modeToken, modeKey);
                totalWins = mode.Win;
                totalLosses = mode.Lose;
                totalGames = mode.Game > 0 ? mode.Game : (mode.Win + mode.Lose);
                totalFinalKills = mode.FinalKills;
                totalFinalDeaths = mode.FinalDeaths;
                totalKills = mode.Kills;
                totalDeaths = mode.Deaths;
                totalBedsBroken = mode.BedDestroy;
                totalBedsLost = mode.BedLose;

                if (mode.UseItem != null)
                {
                    foreach (var item in mode.UseItem)
                    {
                        var key = NormalizeItemKey(item.Key);
                        totalItems[key] = item.Value;
                    }
                }

                if (mode.Upgrade != null)
                {
                    foreach (var upg in mode.Upgrade)
                    {
                        var key = NormalizeUpgradeKey(upg.Key);
                        totalUpgrades[key] = upg.Value;
                    }
                }
            }
        }

        if (modeDisplayName == "总览" && data.BedwarsModes != null)
        {
            foreach (var mode in data.BedwarsModes.Values)
            {
                totalFinalDeaths += mode.FinalDeaths;
                totalKills += mode.Kills;
                totalDeaths += mode.Deaths;
                totalBedsLost += mode.BedLose;

                if (mode.UseItem != null)
                {
                    foreach (var item in mode.UseItem)
                    {
                        string key = NormalizeItemKey(item.Key);
                        if (!totalItems.ContainsKey(key)) totalItems[key] = 0;
                        totalItems[key] += item.Value;
                    }
                }

                if (mode.Upgrade != null)
                {
                    foreach (var upg in mode.Upgrade)
                    {
                        string key = NormalizeUpgradeKey(upg.Key);
                        if (!totalUpgrades.ContainsKey(key)) totalUpgrades[key] = 0;
                        totalUpgrades[key] += upg.Value;
                    }
                }
            }
        }

        if (modeDisplayName == "总览")
        {
            totalLosses = totalGames - totalWins;
            if (totalKills == 0)
            {
                totalKills = data.TotalKills;
            }

            if (totalDeaths == 0)
            {
                totalDeaths = data.TotalDeaths;
            }
        }

        double fkdr = totalFinalDeaths == 0 ? totalFinalKills : (double)totalFinalKills / totalFinalDeaths;
        double kd = totalDeaths == 0 ? totalKills : (double)totalKills / totalDeaths;
        double bblr = totalBedsLost == 0 ? totalBedsBroken : (double)totalBedsBroken / totalBedsLost;
        double winRate = totalGames == 0 ? 0 : (double)totalWins / totalGames * 100;

        // ==================== 黄金400分算法 ====================
        var tacticalItemsCount =
            GetValue(totalItems, "golden_apple") +
            GetValue(totalItems, "ender_pearl") +
            GetValue(totalItems, "fireball") +
            GetValue(totalItems, "tnt");
        var upgradeCount = totalUpgrades.Values.Sum();

        var scoreWr = winRate * 1.8;
        var scoreFkdr = 18.0 * Math.Sqrt(Math.Max(0.0, fkdr));
        var scoreBblr = 20.0 * Math.Max(0.0, bblr);
        var scoreGames = totalGames > 0 ? 10.0 * Math.Log10(totalGames) : 0.0;
        var scoreTactical = tacticalItemsCount > 0 ? 6.0 * Math.Log10(tacticalItemsCount) : 0.0;
        var scoreTeam = upgradeCount > 0 ? 8.0 * Math.Log10(upgradeCount) : 0.0;
        var rawScore = scoreWr + scoreFkdr + scoreBblr + scoreGames + scoreTactical + scoreTeam;

        var coefficient = Math.Min(1.0, 0.30 + (totalGames * 0.003));
        var score = rawScore * coefficient;
        if (double.IsNaN(score) || double.IsInfinity(score)) score = 0;
        score = Math.Clamp(score, 0, 410);

        string tier;
        if (score >= 310) tier = "ACE";
        else if (score >= 270) tier = "S+";
        else if (score >= 230) tier = "S";
        else if (score >= 190) tier = "A+";
        else if (score >= 150) tier = "A";
        else if (score >= 110) tier = "B+";
        else if (score >= 70) tier = "B";
        else tier = "C";
        // ======================================================

        var avatarSrc = ResolveAvatarSrc(customAvatarSrc, fallbackUuid);

        var statusStyle = data.IsBanned
            ? "color:#ef4444; background:#fee2e2;"
            : "color:#059669; background:#d1fae5;";

        string htmlContent = GetHtmlTemplate(
            baseDisplayName,
            data.IsBanned ? "封禁中" : "未被封禁",
            statusStyle,
            avatarSrc,
            BuildBwxpBadgeHtml(bwxpShow),
            totalGames,
            fkdr, totalFinalKills, totalFinalDeaths,
            kd, totalKills, totalDeaths,
            bblr, totalBedsBroken, totalBedsLost,
            winRate, totalWins, totalLosses,
            totalItems, totalUpgrades,
            customBackgroundSrc,
            customSolidBackgroundColor,
            backgroundOpacity,
            chipIconSize,
            playerIdFontSize,
            customTitleBadgeHtml,
            modeDisplayName,
            score, tier // <--- 传入新增的评分参数
        );

        return await RenderStatsCardWithCacheAsync(htmlContent);
    }

    private async Task<Stream> RenderStatsCardWithCacheAsync(string htmlContent)
    {
        var fastHtml = StripSlowExternalResources(htmlContent);
        var cacheKey = ComputeHtmlCacheKey(fastHtml);
        var now = DateTimeOffset.UtcNow;

        if (TryGetCachedRenderedImage(cacheKey, now, out var cachedBytes))
        {
            return new MemoryStream(cachedBytes, writable: false);
        }

        var renderTask = _renderInFlight.GetOrAdd(cacheKey, _ => RenderAndCacheImageBytesAsync(cacheKey, fastHtml));
        try
        {
            var bytes = await renderTask;
            return new MemoryStream(bytes, writable: false);
        }
        finally
        {
            if (renderTask.IsCompleted)
            {
                _renderInFlight.TryRemove(cacheKey, out _);
            }
        }
    }

    private async Task<byte[]> RenderAndCacheImageBytesAsync(string cacheKey, string htmlContent)
    {
        await using var renderedStream = await RenderStatsCardAsync(htmlContent);
        using var ms = new MemoryStream();
        await renderedStream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var now = DateTimeOffset.UtcNow;
        _renderedImageCache[cacheKey] = new RenderedImageCacheEntry
        {
            Bytes = bytes,
            ExpiresAtUtc = now.Add(RenderedImageCacheTtl)
        };
        TrimRenderedImageCacheIfNeeded(now);
        return bytes;
    }

    private bool TryGetCachedRenderedImage(string cacheKey, DateTimeOffset now, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!_renderedImageCache.TryGetValue(cacheKey, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc <= now)
        {
            _renderedImageCache.TryRemove(cacheKey, out _);
            return false;
        }

        bytes = entry.Bytes;
        return true;
    }

    private void TrimRenderedImageCacheIfNeeded(DateTimeOffset now)
    {
        foreach (var kv in _renderedImageCache)
        {
            if (kv.Value.ExpiresAtUtc <= now)
            {
                _renderedImageCache.TryRemove(kv.Key, out _);
            }
        }

        var overflow = _renderedImageCache.Count - RenderedImageCacheMaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        var keysToRemove = _renderedImageCache
            .OrderBy(kvp => kvp.Value.ExpiresAtUtc)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var key in keysToRemove)
        {
            _renderedImageCache.TryRemove(key, out _);
        }
    }

    private static string StripSlowExternalResources(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return htmlContent;
        }

        return GoogleFontImportRegex.Replace(htmlContent, string.Empty);
    }

    private static string ComputeHtmlCacheKey(string htmlContent)
    {
        var bytes = Encoding.UTF8.GetBytes(htmlContent);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task<Stream> RenderStatsCardAsync(string htmlContent)
    {
        Exception? lastError = null;

        await _renderLock.WaitAsync();
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await EnsureBrowserReadyAsync();
                    if (_browser == null)
                    {
                        throw new InvalidOperationException("浏览器未初始化。");
                    }

                    if (_renderPage == null)
                    {
                        _renderPage = await _browser.NewPageAsync();
                    }

                    await _renderPage.SetViewportAsync(new ViewPortOptions { Width = 1100, Height = 1000 });
                    await WithTimeout(_renderPage.SetContentAsync(htmlContent), TimeSpan.FromSeconds(25), "页面渲染超时");

                    var cardElement = await WithTimeout(_renderPage.QuerySelectorAsync(".card"), TimeSpan.FromSeconds(10), "卡片节点查询超时");
                    if (cardElement == null)
                    {
                        throw new InvalidOperationException("战绩卡片渲染失败：未找到卡片节点。");
                    }

                    return await WithTimeout(
                        cardElement.ScreenshotStreamAsync(new ElementScreenshotOptions
                        {
                            Type = ScreenshotType.Jpeg,
                            Quality = 86
                        }),
                        TimeSpan.FromSeconds(10),
                        "截图超时");
                }
                catch (Exception ex) when (attempt == 1 && IsRecoverableBrowserError(ex))
                {
                    lastError = ex;
                    Console.WriteLine($"[渲染器] 浏览器异常，正在自动重启后重试: {ex.Message}");
                    await CloseRenderPageNoThrowAsync();
                    await EnsureBrowserReadyAsync(forceRestart: true);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await CloseRenderPageNoThrowAsync();
                    break;
                }
            }

            throw lastError ?? new Exception("图片渲染失败");
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private bool IsRecoverableBrowserError(Exception ex)
    {
        if (_browser == null || !_browser.IsConnected)
        {
            return true;
        }

        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("Session closed", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("browser has disconnected", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("Most likely the page has been closed", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout, string timeoutMessage)
    {
        var finishedTask = await Task.WhenAny(task, Task.Delay(timeout));
        if (finishedTask != task)
        {
            throw new TimeoutException(timeoutMessage);
        }

        await task;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string timeoutMessage)
    {
        var finishedTask = await Task.WhenAny(task, Task.Delay(timeout));
        if (finishedTask != task)
        {
            throw new TimeoutException(timeoutMessage);
        }

        return await task;
    }

    private static string ResolveDisplayName(PlayerData data, string? queriedPlayerName)
    {
        if (!string.IsNullOrWhiteSpace(queriedPlayerName))
        {
            return queriedPlayerName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(data.PlayerName))
        {
            return data.PlayerName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(data.Username))
        {
            return data.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(data.Name))
        {
            return data.Name.Trim();
        }

        return "Unknown";
    }

    private static string ResolveAvatarSrc(string? customAvatarSrc, string? fallbackUuid)
    {
        if (!string.IsNullOrWhiteSpace(customAvatarSrc)) return customAvatarSrc;

        var compactDefaultUuid = DefaultOfficialAvatarUuid.Replace("-", string.Empty);
        return $"https://skins.mcstats.com/face/{compactDefaultUuid}";
    }

    private static int GetValue(Dictionary<string, int> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value : 0;
    }

    private string EnsureIconConfigFile()
    {
        var configDirectory = ResolveIconsConfigDirectory();
        var configPath = Path.Combine(configDirectory, IconsConfigFileName);

        try
        {
            if (!Directory.Exists(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            if (!File.Exists(configPath))
            {
                var json = CreateDefaultIconConfigJson();
                File.WriteAllText(configPath, json, Encoding.UTF8);
            }
        }
        catch
        {
            return configPath;
        }

        return configPath;
    }

    private static string ResolveIconsConfigDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, IconsConfigDirectoryName);
            if (Directory.Exists(candidate) || File.Exists(Path.Combine(dir.FullName, "BedwarsBot.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, IconsConfigDirectoryName);
    }

    private static string ResolveHtmlTemplateDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, HtmlTemplateDirectoryName);
            if (Directory.Exists(candidate) || File.Exists(Path.Combine(dir.FullName, "BedwarsBot.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, HtmlTemplateDirectoryName);
    }

    private string EnsureHtmlTemplateFile()
    {
        var htmlDirectory = ResolveHtmlTemplateDirectory();
        var htmlPath = Path.Combine(htmlDirectory, HtmlTemplateFileName);
        var repaired = false;

        try
        {
            if (!Directory.Exists(htmlDirectory))
            {
                Directory.CreateDirectory(htmlDirectory);
            }

            if (!File.Exists(htmlPath))
            {
                File.WriteAllText(htmlPath, GetBuiltinHtmlTemplate(), Encoding.UTF8);
                repaired = true;
            }
            else
            {
                var existing = File.ReadAllText(htmlPath);
                if (IsLegacyOrBrokenTemplate(existing))
                {
                    File.WriteAllText(htmlPath, GetBuiltinHtmlTemplate(), Encoding.UTF8);
                    repaired = true;
                }
            }
        }
        catch
        {
            return htmlPath;
        }

        Console.WriteLine(repaired
            ? $"[模板] 已自动修复/重建: {htmlPath}"
            : $"[模板] 使用现有模板: {htmlPath}");

        return htmlPath;
    }

    private static bool IsLegacyOrBrokenTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return true;
        if (template.Contains("{{cardStyleAttr}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{cardStyle}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{bwxpBadgeHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{customTitleBadgeHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{kd}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{kills}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{deaths}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{itemsHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{upgradesHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains(".card", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{cardClass}}", StringComparison.Ordinal)) return true;
        if (!template.Contains(".stats-grid", StringComparison.Ordinal)) return true;
        if (!template.Contains(".resources-section", StringComparison.Ordinal)) return true;
        if (!template.Contains(".score-board", StringComparison.Ordinal)) return true;
        if (!template.Contains(".card.has-bg", StringComparison.Ordinal)) return true;
        if (!template.Contains("--card-bg", StringComparison.Ordinal)) return true;
        if (!template.Contains("var(--card-bg)", StringComparison.Ordinal)) return true;
        // Repair templates that were damaged by encoding/mojibake and end up with broken HTML tags.
        if (template.Contains("?/span", StringComparison.Ordinal)) return true;
        if (template.Contains("鏈€", StringComparison.Ordinal) || template.Contains("鎴", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string GetBuiltinHtmlTemplate()
    {
        return """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@500;700;900&family=Nunito:wght@700;800&display=swap');
        {{customFontFaceCss}}
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: {{globalFontFamily}}; background: #fff; display: flex; justify-content: center; align-items: center; min-height: 100vh; }
        .card { --bg-opacity: 0.22; --chip-icon-size: 28px; --player-id-size: 14px; --card-bg: none; --solid-bg-color: #ffffff; width: 1050px; background: #fff; border-radius: 24px; overflow: hidden; border: 1px solid rgba(15,23,42,0.16); box-shadow: 0 16px 36px rgba(15,23,42,0.14); position: relative; }
        .card.has-bg { background: rgba(255,255,255,0.86); }
        .card.has-bg::before { content: ''; position: absolute; inset: 0; background-image: var(--card-bg); background-size: cover; background-position: center; opacity: var(--bg-opacity, 0.22); z-index: 0; }
        .card > * { position: relative; z-index: 1; }
        .card.has-image-bg .header, .card.has-image-bg .stat-group, .card.has-image-bg .resources-section, .card.has-image-bg .ratio-box, .card.has-image-bg .sub-stats, .card.has-image-bg .chips-container { background: rgba(255,255,255,0.60) !important; }
        .card.has-solid-bg .header { background: var(--solid-bg-color) !important; }
        .header { padding: 32px 40px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #eef2f7; }
        .user-section { display: flex; align-items: center; gap: 18px; }
        .avatar { width: 86px; height: 86px; border-radius: 18px; border: 2px solid rgba(255,255,255,.8); box-shadow: 0 6px 14px rgba(0,0,0,.1); }
        .user-info-top { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; }
        .custom-title-badge { display: inline-block; font-size: 26px; font-weight: 900; line-height: 1; letter-spacing: .5px; text-shadow: 0 1px 1px rgba(0,0,0,.18); }
        .user-info h1 { font-size: 34px; color: #1f2937; line-height: 1; margin-bottom: 0; font-weight: 900; }
        .mc-level-badge { display: inline-flex; align-items: center; background: rgba(15,23,42,.85); padding: 4px 12px; border-radius: 10px; font-family: {{globalFontFamily}}; font-weight: 900; font-size: 17px; letter-spacing: 1px; box-shadow: inset 0 1px 1px rgba(255,255,255,.2), 0 4px 10px rgba(0,0,0,.15); border: 1px solid rgba(255,255,255,.1); transform: translateY(-2px); }
        .player-id { font-size: var(--player-id-size, 14px); font-weight: 800; color: #374151; line-height: 1.2; margin-top: 2px; }
        .mode-center { text-align: center; color: #000; font-size: 34px; font-weight: 900; line-height: 1; letter-spacing: .3px; padding: 10px 0 4px; }
        .score-board { display: flex; flex-direction: column; align-items: center; background: linear-gradient(135deg, rgba(99,102,241,.1), rgba(139,92,246,.05)); border: 1px solid rgba(99,102,241,.2); padding: 8px 24px; border-radius: 16px; }
        .score-label { font-size: 12px; color: #6366f1; font-weight: 900; letter-spacing: 1.2px; margin-bottom: 2px; }
        .score-value { font-size: 34px; font-weight: 900; line-height: 1; font-family: {{globalFontFamily}}; color: #4f46e5; display: flex; align-items: center; gap: 8px; }
        .score-tier { font-size: 14px; font-weight: 900; color: #fff; background: linear-gradient(135deg,#fbbf24,#f59e0b); padding: 2px 10px; border-radius: 8px; }
        .stats-grid { display: grid; grid-template-columns: repeat(4, 1fr); border-bottom: 1px solid rgba(0,0,0,.05); }
        .stat-group { padding: 36px 24px; border-right: 1px solid rgba(0,0,0,.05); background: #fff; display: flex; flex-direction: column; align-items: center; }
        .stat-group:last-child { border-right: none; }
        .group-title { font-size: 18px; font-weight: 900; margin-bottom: 18px; color: #64748b; }
        .ratio-box { margin-bottom: 18px; text-align: center; padding: 10px 14px; border-radius: 14px; background: #fff; border: 1px solid rgba(15,23,42,.08); box-shadow: 0 8px 16px rgba(15,23,42,.08); width: 100%; }
        .ratio-val { font-size: 52px; font-weight: 800; line-height: 1; font-family: {{globalFontFamily}}; color: #334155; }
        .ratio-label { font-size: 15px; color: #64748b; font-weight: 800; margin-top: 6px; }
        .sub-stats { display: flex; justify-content: space-between; width: 100%; background: #fff; padding: 14px; border-radius: 12px; border: 1px solid rgba(15,23,42,.08); }
        .sub-item { display: flex; flex-direction: column; align-items: center; flex: 1; }
        .sub-val { font-size: 22px; font-weight: 800; color: #334155; font-family: {{globalFontFamily}}; }
        .sub-label { font-size: 14px; color: #94a3b8; font-weight: 700; margin-top: 3px; }
        .resources-section { padding: 30px 34px 44px; display: grid; grid-template-columns: 1fr 1fr; gap: 32px; background: #fff; }
        .res-title { font-size: 16px; font-weight: 900; color: #475569; margin-bottom: 14px; display: flex; align-items: center; }
        .res-title::before { content: ''; display: inline-block; width: 5px; height: 18px; background: #6366f1; margin-right: 10px; border-radius: 4px; }
        .chips-container { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 12px; min-height: 180px; }
        .chip { display: flex; align-items: center; justify-content: space-between; padding: 10px 14px; border-radius: 10px; font-size: 14px; font-weight: 700; border: 1px solid; background: #fff; }
        .chip-left { display: flex; align-items: center; gap: 10px; min-width: 0; }
        .chip-icon { width: var(--chip-icon-size, 28px); height: var(--chip-icon-size, 28px); object-fit: contain; flex: 0 0 var(--chip-icon-size, 28px); display: block; }
        .chip-name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .chip-count { font-weight: 900; padding-left: 8px; border-left: 1px solid rgba(0,0,0,.1); font-family: {{globalFontFamily}}; }
        .chip-item { background: #fffbeb; border-color: #fcd34d; color: #b45309; }
        .chip-upgrade { background: #eff6ff; border-color: #bfdbfe; color: #1d4ed8; }
    </style>
</head>
<body>
    <div class="{{cardClass}}" style="{{cardStyle}}">
        <div class="header">
            <div class="user-section">
                <img src="{{avatarSrc}}" class="avatar">
                <div class="user-info">
                    <div class="user-info-top">
                        {{customTitleBadgeHtml}}
                        {{bwxpBadgeHtml}}
                    </div>
                    <div class="player-id">{{name}}</div>
                </div>
            </div>
            <div class="score-board">
                <div class="score-label">RATING 综合评分</div>
                <div class="score-value">{{score}} <span class="score-tier">{{tier}}</span></div>
            </div>
            <div style="text-align:right;">
                <div style="font-size:12px;color:#9ca3af;font-weight:700;">总场次 TOTAL GAMES</div>
                <div style="font-size:30px;font-weight:900;color:#374151;font-family:{{globalFontFamily}};">{{games}}</div>
            </div>
        </div>
        <div class="mode-center">{{modeDisplayName}}</div>
        <div class="stats-grid">
            <div class="stat-group">
                <div class="group-title">战斗数据 COMBAT</div>
                <div class="ratio-box"><div class="ratio-val">{{fkdr}}</div><div class="ratio-label">FKDR (最终杀比)</div></div>
                <div class="sub-stats"><div class="sub-item"><span class="sub-val">{{fk}}</span><span class="sub-label">最终击杀</span></div><div class="sub-item"><span class="sub-val">{{fd}}</span><span class="sub-label">最终死亡</span></div></div>
            </div>
            <div class="stat-group">
                <div class="group-title">击杀数据 K/D</div>
                <div class="ratio-box"><div class="ratio-val">{{kd}}</div><div class="ratio-label">KD (击杀死亡比)</div></div>
                <div class="sub-stats"><div class="sub-item"><span class="sub-val">{{kills}}</span><span class="sub-label">击杀</span></div><div class="sub-item"><span class="sub-val">{{deaths}}</span><span class="sub-label">死亡</span></div></div>
            </div>
            <div class="stat-group">
                <div class="group-title">床战数据 BEDS</div>
                <div class="ratio-box"><div class="ratio-val">{{bblr}}</div><div class="ratio-label">BBLR (毁床比)</div></div>
                <div class="sub-stats"><div class="sub-item"><span class="sub-val">{{bb}}</span><span class="sub-label">拆床</span></div><div class="sub-item"><span class="sub-val">{{bl}}</span><span class="sub-label">被拆</span></div></div>
            </div>
            <div class="stat-group">
                <div class="group-title">胜负数据 SESSION</div>
                <div class="ratio-box"><div class="ratio-val">{{wr}}%</div><div class="ratio-label">WIN RATE (胜率)</div></div>
                <div class="sub-stats"><div class="sub-item"><span class="sub-val">{{wins}}</span><span class="sub-label">胜利</span></div><div class="sub-item"><span class="sub-val">{{losses}}</span><span class="sub-label">失败</span></div></div>
            </div>
        </div>
        <div class="resources-section">
            <div><div class="res-title">物品消耗 ITEMS</div><div class="chips-container">{{itemsHtml}}</div></div>
            <div><div class="res-title">升级购买 UPGRADES</div><div class="chips-container">{{upgradesHtml}}</div></div>
        </div>
    </div>
</body>
</html>
""";
    }

    private string GetHtmlTemplateText()
    {
        var templatePath = EnsureHtmlTemplateFile();
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"缺少模板文件: {templatePath}");
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(templatePath);
        if (_htmlTemplateCache != null && lastWriteUtc <= _htmlTemplateLastWriteUtc)
        {
            return _htmlTemplateCache;
        }

        var html = File.ReadAllText(templatePath);
        _htmlTemplateCache = html;
        _htmlTemplateLastWriteUtc = lastWriteUtc;
        return html;
    }

    private static string CreateDefaultIconConfigJson()
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DefaultItemsOrder)
        {
            if (!items.ContainsKey(key)) items[key] = string.Empty;
        }
        foreach (var aliasKey in ItemKeyAliases.Keys)
        {
            if (!items.ContainsKey(aliasKey)) items[aliasKey] = string.Empty;
        }

        var upgrades = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DefaultUpgradesOrder)
        {
            if (!upgrades.ContainsKey(key)) upgrades[key] = string.Empty;
        }
        foreach (var aliasKey in new[] { "haste", "counter_attack_trap" })
        {
            if (!upgrades.ContainsKey(aliasKey)) upgrades[aliasKey] = string.Empty;
        }

        var obj = new { items, upgrades };
        return JsonConvert.SerializeObject(obj, Formatting.Indented);
    }

    private IconConfig GetIconConfig()
    {
        var configPath = EnsureIconConfigFile();
        if (!File.Exists(configPath)) return new IconConfig();

        var lastWriteUtc = File.GetLastWriteTimeUtc(configPath);
        if (_iconConfig != null && lastWriteUtc <= _iconConfigLastWriteUtc) return _iconConfig;

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonConvert.DeserializeObject<IconConfig>(json) ?? new IconConfig();
            if (EnsureIconConfigKeys(config))
            {
                var merged = new { items = config.Items, upgrades = config.Upgrades };
                File.WriteAllText(configPath, JsonConvert.SerializeObject(merged, Formatting.Indented), Encoding.UTF8);
            }
            config.BaseDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
            config.Normalize();
            _iconConfig = config;
            _iconConfigLastWriteUtc = File.GetLastWriteTimeUtc(configPath);
            return config;
        }
        catch
        {
            return new IconConfig();
        }
    }

    private static bool EnsureIconConfigKeys(IconConfig config)
    {
        config.Items ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.Upgrades ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var changed = false;

        foreach (var key in DefaultItemsOrder)
        {
            if (!config.Items.ContainsKey(key))
            {
                config.Items[key] = string.Empty;
                changed = true;
            }
        }

        foreach (var aliasKey in ItemKeyAliases.Keys)
        {
            if (!config.Items.ContainsKey(aliasKey))
            {
                config.Items[aliasKey] = string.Empty;
                changed = true;
            }
        }

        foreach (var key in DefaultUpgradesOrder)
        {
            if (!config.Upgrades.ContainsKey(key))
            {
                config.Upgrades[key] = string.Empty;
                changed = true;
            }
        }

        foreach (var aliasKey in new[] { "haste", "counter_attack_trap" })
        {
            if (!config.Upgrades.ContainsKey(aliasKey))
            {
                config.Upgrades[aliasKey] = string.Empty;
                changed = true;
            }
        }

        return changed;
    }

    private static string? ResolveIconSrc(IconConfig config, Dictionary<string, string> iconMap, string key)
    {
        if (!iconMap.TryGetValue(key, out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var trimmed = rawPath.Trim();

        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith("."))
        {
            var fullPath = Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(config.BaseDirectory, trimmed));

            if (!File.Exists(fullPath)) return null;
            return BuildDataUriFromFile(fullPath);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "data")
            {
                return uri.AbsoluteUri;
            }

            if (uri.Scheme == Uri.UriSchemeFile && File.Exists(uri.LocalPath))
            {
                return BuildDataUriFromFile(uri.LocalPath);
            }
        }

        var fallbackPath = Path.GetFullPath(Path.Combine(config.BaseDirectory, trimmed));
        if (!File.Exists(fallbackPath)) return null;
        return BuildDataUriFromFile(fallbackPath);
    }

    private static string? BuildDataUriFromFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0) return null;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream"
            };

            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeItemKey(string key)
    {
        var lower = key.ToLower();
        return ItemKeyAliases.TryGetValue(lower, out var canonical) ? canonical : lower;
    }

    private static string NormalizeUpgradeKey(string key)
    {
        var lower = key.ToLower();
        return lower switch
        {
            "haste" => "fast_dig",
            "counter_attack_trap" => "counter_offensive_trap",
            "counter_offensive_trap" => "counter_offensive_trap",
            _ => lower
        };
    }

    private static string? ResolveModeKey(Dictionary<string, BedwarsModeStats>? modes, string rawMode)
    {
        if (string.IsNullOrWhiteSpace(rawMode))
        {
            return null;
        }

        var token = rawMode.Trim().ToLowerInvariant();
        token = token.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);

        if (token is "solo" or "1s" or "bw1" or "单八")
        {
            if (modes != null && modes.ContainsKey("bw1")) return "bw1";
            if (modes != null && modes.ContainsKey("bw8")) return "bw8";
            if (modes != null && modes.ContainsKey("bw16")) return "bw16";
            return "bw1";
        }

        if (token is "2s" or "duo" or "double" or "doubles" or "双八")
        {
            if (modes != null && modes.ContainsKey("bw8")) return "bw8";
            if (modes != null && modes.ContainsKey("bw16")) return "bw16";
            return "bw8";
        }

        if (token is "4s" or "squad" or "44")
        {
            if (modes != null && modes.ContainsKey("bw16")) return "bw16";
            if (modes != null && modes.ContainsKey("bw8")) return "bw8";
            return "bw16";
        }

        if (ModeAliasToKey.TryGetValue(token, out var mapped))
        {
            return mapped;
        }

        if (token == "all")
        {
            return "all";
        }

        if (token.All(char.IsDigit))
        {
            var bw = $"bw{token}";
            if (modes != null && modes.ContainsKey(bw)) return bw;

            var bwxp = $"bwxp{token}";
            if (modes != null && modes.ContainsKey(bwxp)) return bwxp;
        }

        if (token.StartsWith("xp", StringComparison.OrdinalIgnoreCase))
        {
            var xp = $"bwxp{token[2..]}";
            if (modes != null && modes.ContainsKey(xp)) return xp;
        }

        if (token.StartsWith("bw", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = token;
            if (modes != null && modes.ContainsKey(normalized)) return normalized;
        }

        if (modes == null || modes.Count == 0)
        {
            return null;
        }

        return modes.Keys.FirstOrDefault(k => string.Equals(k, rawMode, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetModeDisplayName(string? requestedToken, string modeKey)
    {
        var token = (requestedToken ?? string.Empty).Trim().ToLowerInvariant();
        token = token.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        if (token.Length > 0)
        {
            return token switch
            {
                "all" or "total" or "overall" or "总览" or "全部" => "总览",
                "solo" or "1s" or "bw1" or "单八" => "单人八队",
                "2s" or "duo" or "double" or "doubles" or "双八" or "bw8" => "双人八队",
                "4s" or "squad" or "44" or "bw16" or "46" or "64" => "四人四队",
                "xp" or "xp32" or "48" or "bwxp32" => "经验模式(32人)",
                "xp64" or "bwxp64" => "经验模式(64人)",
                "xp8x4" or "bwxp8x4" => "经验模式(8x4)",
                _ => GetModeDisplayNameByResolvedKey(modeKey)
            };
        }

        return GetModeDisplayNameByResolvedKey(modeKey);
    }

    private static string GetModeDisplayNameByResolvedKey(string modeKey)
    {
        var key = modeKey.ToLowerInvariant();
        return key switch
        {
            "bw1" => "单人八队",
            "bw8" => "双人八队",
            "bw16" => "四人四队",
            "bwxp32" => "经验模式(32人)",
            "bwxp64" => "经验模式(64人)",
            "bwxp8x4" => "经验模式(8x4)",
            "bw999" => "娱乐模式",
            _ => modeKey
        };
    }

    private string GenerateChipsHtml(
        Dictionary<string, int> dict,
        string cssClass,
        IReadOnlyList<string> orderedKeys,
        IconConfig iconConfig,
        Dictionary<string, string> iconMap)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AppendChip(string rawName, int value)
        {
            if (cssClass == "chip-item" && HiddenItemKeys.Contains(rawName)) return;
            if (cssClass == "chip-upgrade" && HiddenUpgradeKeys.Contains(rawName)) return;

            string displayName = _cnDict.ContainsKey(rawName) ? _cnDict[rawName] : rawName;
            if (!_cnDict.ContainsKey(rawName))
                displayName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(displayName);

            var iconSrc = ResolveIconSrc(iconConfig, iconMap, rawName);
            var iconHtml = string.IsNullOrWhiteSpace(iconSrc)
                ? string.Empty
                : $"<img class='chip-icon' src='{iconSrc}' alt='' onerror=\"this.style.display='none';this.removeAttribute('src');\">";

            sb.Append($@"
                <div class='chip {cssClass}'>
                    <span class='chip-left'>{iconHtml}<span class='chip-name'>{displayName}</span></span>
                    <span class='chip-count'>{value}</span>
                </div>");
        }

        foreach (var key in orderedKeys)
        {
            var rawName = key.ToLower();
            if (!seen.Add(rawName)) continue;
            dict.TryGetValue(rawName, out var value);
            AppendChip(rawName, value);
        }

        foreach (var kvp in dict.OrderByDescending(x => x.Value))
        {
            var rawName = kvp.Key.ToLower();
            if (!seen.Add(rawName)) continue;
            AppendChip(rawName, kvp.Value);
        }

        return sb.ToString();
    }

    private string GetHtmlTemplate(
        string name, string statusText, string statusStyle, string avatarSrc, string bwxpBadgeHtml, int games,
        double fkdr, int fk, int fd,
        double kd, int kills, int deaths,
        double bblr, int bb, int bl,
        double wr, int wins, int losses,
        Dictionary<string, int> items, Dictionary<string, int> upgrades,
        string? backgroundSrc,
        string? solidBackgroundColor,
        double backgroundOpacity,
        int chipIconSize,
        int playerIdFontSize,
        string? customTitleBadgeHtml,
        string modeDisplayName,
        double score, string tier) // <--- 新增接收评分的参数
    {
        var iconConfig = GetIconConfig();
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();
        var hasImageBg = !string.IsNullOrWhiteSpace(backgroundSrc);
        var hasSolidBg = !hasImageBg && !string.IsNullOrWhiteSpace(solidBackgroundColor);
        var cardClass = hasImageBg ? "card has-bg has-image-bg" : (hasSolidBg ? "card has-solid-bg" : "card");
        var opacityValue = Math.Clamp(backgroundOpacity, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
        var iconSizeValue = Math.Clamp(chipIconSize, 16, 40);
        var idSizeValue = Math.Clamp(playerIdFontSize, 12, 36);
        var solidBgStyle = BuildSolidBackgroundStyle(solidBackgroundColor);
        var cardStyle = !hasImageBg
            ? $"--bg-opacity: {opacityValue}; --chip-icon-size: {iconSizeValue}px; --player-id-size: {idSizeValue}px;{solidBgStyle}"
            : $"--card-bg: url('{backgroundSrc}'); --bg-opacity: {opacityValue}; --chip-icon-size: {iconSizeValue}px; --player-id-size: {idSizeValue}px;";
        string itemsHtml = GenerateChipsHtml(items, "chip-item", DefaultItemsOrder, iconConfig, iconConfig.ItemsNormalized);
        string upgradesHtml = GenerateChipsHtml(upgrades, "chip-upgrade", DefaultUpgradesOrder, iconConfig, iconConfig.UpgradesNormalized);
        var template = GetHtmlTemplateText();
        return template
            .Replace("{{customFontFaceCss}}", customFontFaceCss)
            .Replace("{{customBadgeFontFamily}}", globalFontFamily)
            .Replace("{{globalFontFamily}}", globalFontFamily)
            .Replace("{{cardClass}}", cardClass)
            .Replace("{{cardStyle}}", cardStyle)
            .Replace("{{avatarSrc}}", avatarSrc)
            .Replace("{{name}}", name)
            .Replace("{{customTitleBadgeHtml}}", customTitleBadgeHtml ?? string.Empty)
            .Replace("{{bwxpBadgeHtml}}", bwxpBadgeHtml)
            .Replace("{{modeDisplayName}}", modeDisplayName)
            .Replace("{{statusStyle}}", statusStyle)
            .Replace("{{statusText}}", statusText)
            .Replace("{{score}}", score.ToString("F1", CultureInfo.CurrentCulture))
            .Replace("{{tier}}", tier)
            .Replace("{{games}}", games.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{fkdr}}", fkdr.ToString("F2", CultureInfo.CurrentCulture))
            .Replace("{{fk}}", fk.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{fd}}", fd.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{kd}}", kd.ToString("F2", CultureInfo.CurrentCulture))
            .Replace("{{kills}}", kills.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{deaths}}", deaths.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{bblr}}", bblr.ToString("F2", CultureInfo.CurrentCulture))
            .Replace("{{bb}}", bb.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{bl}}", bl.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{wr}}", wr.ToString("F1", CultureInfo.CurrentCulture))
            .Replace("{{wins}}", wins.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{losses}}", losses.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{itemsHtml}}", itemsHtml)
            .Replace("{{upgradesHtml}}", upgradesHtml);
    }

    private static string BuildSolidBackgroundStyle(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return string.Empty;
        }

        var raw = color.Trim().TrimStart('#');
        if (raw.Length is not (3 or 6))
        {
            return string.Empty;
        }

        foreach (var ch in raw)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return string.Empty;
            }
        }

        return $" --solid-bg-color: #{raw};";
    }

    private static string BuildBwxpBadgeHtml(string? rawBwxpShow)
    {
        if (string.IsNullOrWhiteSpace(rawBwxpShow))
        {
            return string.Empty;
        }

        var text = rawBwxpShow.Trim();
        var sb = new StringBuilder();
        var currentColor = "#FFFFFF";
        var isBold = false;
        var isItalic = false;
        var isUnderlined = false;
        var isStrikethrough = false;
        var isObfuscated = false;
        var hasContent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '§' && i + 1 < text.Length)
            {
                var code = char.ToLowerInvariant(text[i + 1]);
                i++;

                if (McColorMap.TryGetValue(code, out var mappedColor))
                {
                    currentColor = mappedColor;
                    isBold = false;
                    isItalic = false;
                    isUnderlined = false;
                    isStrikethrough = false;
                    isObfuscated = false;
                    continue;
                }

                if (code == 'r')
                {
                    currentColor = "#FFFFFF";
                    isBold = false;
                    isItalic = false;
                    isUnderlined = false;
                    isStrikethrough = false;
                    isObfuscated = false;
                    continue;
                }

                if (code == 'l') { isBold = true; continue; }
                if (code == 'o') { isItalic = true; continue; }
                if (code == 'n') { isUnderlined = true; continue; }
                if (code == 'm') { isStrikethrough = true; continue; }
                if (code == 'k') { isObfuscated = true; continue; }

                continue;
            }

            var renderChar = isObfuscated ? GetObfuscatedChar(ch) : ch;
            if (renderChar == '\0')
            {
                continue;
            }

            var encoded = WebUtility.HtmlEncode(renderChar.ToString());
            if (string.IsNullOrEmpty(encoded))
            {
                continue;
            }

            var extraStyle = currentColor == "#FFFF55"
                ? "text-shadow:0 0 5px rgba(255,255,85,0.6),0 1px 3px rgba(0,0,0,0.8);"
                : "text-shadow:0 1px 3px rgba(0,0,0,0.8);";
            var style = new StringBuilder();
            style.Append("color:").Append(currentColor).Append(';');
            style.Append(extraStyle);
            if (isBold) style.Append("font-weight:900;");
            if (isItalic) style.Append("font-style:italic;");
            if (isUnderlined && isStrikethrough) style.Append("text-decoration:underline line-through;");
            else if (isUnderlined) style.Append("text-decoration:underline;");
            else if (isStrikethrough) style.Append("text-decoration:line-through;");

            sb.Append($"<span style=\"{style}\">{encoded}</span>");
            hasContent = true;
        }

        if (!hasContent)
        {
            return string.Empty;
        }

        return $"<div class=\"mc-level-badge\">{sb}</div>";
    }

    private static char GetObfuscatedChar(char source)
    {
        if (char.IsWhiteSpace(source))
        {
            return source;
        }

        if (char.IsDigit(source))
        {
            return (char)('0' + Random.Shared.Next(0, 10));
        }

        if (char.IsLetter(source))
        {
            const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            return letters[Random.Shared.Next(letters.Length)];
        }

        const string symbols = "★☆✦✧✩⚝✪✫";
        return symbols[Random.Shared.Next(symbols.Length)];
    }
}



