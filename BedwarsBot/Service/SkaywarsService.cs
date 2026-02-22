using System.Globalization;
using System.Net;
using System.Text;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PuppeteerSharp;

namespace BedwarsBot;

public class SkaywarsService
{
    private const string SwTranslationBuild = "SW-TRANS-20260218-04";
    private const string DefaultAvatarName = "Steve";
    private const string IconsConfigDirectoryName = "pz";
    private const string IconsConfigFileName = "skywars-icons.json";
    private const string HtmlTemplateDirectoryName = "HTML";
    private const string HtmlTemplateFileName = "sw-stats-template.html";

    private static readonly string[] DefaultUseItemsOrder =
    {
        "TNT",
        "GOLDEN_APPLE",
        "ENCHANTED_GOLDEN_APPLE"
    };

    private static readonly string[] DefaultSpecialItemsOrder =
    {
        "ENCHANTED_GOLDEN_APPLE",
        "END_CRYSTAL",
        "TOTEM",
        "GOLDEN_APPLE"
    };

    private static readonly string[] DefaultEffectOrder =
    {
        "KIT",
        "KILLSOUND",
        "PARTICLEEFFECT"
    };

    private static readonly Dictionary<string, string> ItemDisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TNT"] = "TNT炸药",
        ["GOLDEN_APPLE"] = "普通金苹果",
        ["GOLDENAPPLE"] = "普通金苹果",
        ["ENCHANTED_GOLDEN_APPLE"] = "附魔金苹果",
        ["ENCHANTEDGOLDENAPPLE"] = "附魔金苹果",
        ["END_CRYSTAL"] = "末影水晶",
        ["ENDCRYSTAL"] = "末影水晶",
        ["TOTEM"] = "不死图腾",
        ["SLIME_BALL"] = "击退球",
        ["SLIMEBALL"] = "击退球"
    };

    private static readonly Dictionary<string, string> EffectDisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KIT"] = "职业",
        ["KILLSOUND"] = "击杀音效",
        ["KILL_SOUND"] = "击杀音效",
        ["PARTICLEEFFECT"] = "粒子特效",
        ["PARTICLE_EFFECT"] = "粒子特效",
        ["GLASSCOLOR"] = "玻璃颜色",
        ["GLASS_COLOR"] = "玻璃颜色",
        ["PROJECTILEEFFECT"] = "抛射物特效",
        ["PROJECTILE_EFFECT"] = "抛射物特效",
        ["WINSOUND"] = "胜利音效",
        ["WIN_SOUND"] = "胜利音效",
        ["WIN_"] = "胜利音效",
        ["WIN"] = "胜利音效"
    };

    private static readonly Dictionary<string, string> KillSoundDisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "无",
        ["explosion"] = "爆炸",
        ["zombiehorsedeath"] = "僵尸马死亡音效",
        ["zombiehorse_death"] = "僵尸马死亡音效",
        ["enderdragondeath"] = "末影龙死亡音效",
        ["enderdragon_death"] = "末影龙死亡音效",
        ["ghasthurt"] = "恶魂受伤音效",
        ["ghast_hurt"] = "恶魂受伤音效",
        ["menu"] = "菜单音效"
    };

    private static readonly Dictionary<string, string> ParticleEffectDisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "无",
        ["green"] = "绿色",
        ["happy"] = "开心粒子",
        ["critical"] = "暴击粒子"
    };

    private static readonly Dictionary<string, string> GenericEffectValueDisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "无",
        ["green"] = "绿色",
        ["lightgray"] = "浅灰色",
        ["light_gray"] = "浅灰色",
        ["happy"] = "开心粒子",
        ["critical"] = "暴击粒子",
        ["menu"] = "菜单音效",
        ["ghasthurt"] = "恶魂受伤音效",
        ["ghast_hurt"] = "恶魂受伤音效",
        ["zombiehorsedeath"] = "僵尸马死亡音效",
        ["zombiehorse_death"] = "僵尸马死亡音效",
        ["enderdragondeath"] = "末影龙死亡音效",
        ["enderdragon_death"] = "末影龙死亡音效"
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

    private static readonly Dictionary<string, string> KitDisplayNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enderman"] = "末影人",
        ["skeleton"] = "骷髅",
        ["zombie"] = "僵尸",
        ["creeper"] = "苦力怕",
        ["blaze"] = "烈焰人",
        ["pigman"] = "僵尸猪灵",
        ["witch"] = "女巫",
        ["farmer"] = "农夫",
        ["knight"] = "骑士",
        ["scout"] = "斥候",
        ["healer"] = "治疗师",
        ["archer"] = "弓箭手",
        ["pyro"] = "火焰术士",
        ["armorer"] = "护甲师",
        ["default"] = "默认职业",
        ["none"] = "无职业",
        ["unknown"] = "未知职业"
    };

    private static readonly ConcurrentDictionary<string, string> AiItemDisplayNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AiKitDisplayNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AiEffectDisplayNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AiEffectValueMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SwDeepSeekTranslator DeepSeekTranslator = new();

    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLifecycleLock = new(1, 1);
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

    private sealed class IconConfig
    {
        public Dictionary<string, string>? UseItems { get; set; }
        public Dictionary<string, string>? SpecialItems { get; set; }

        [JsonIgnore]
        public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

        [JsonIgnore]
        public Dictionary<string, string> UseItemsNormalized { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public Dictionary<string, string> SpecialItemsNormalized { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        public void Normalize()
        {
            UseItemsNormalized = NormalizeDictionary(UseItems);
            SpecialItemsNormalized = NormalizeDictionary(SpecialItems);
        }

        private static Dictionary<string, string> NormalizeDictionary(Dictionary<string, string>? source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (var entry in source)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                result[NormalizeItemKey(entry.Key)] = entry.Value;
            }

            return result;
        }
    }

    public async Task InitializeAsync()
    {
        Console.WriteLine("正在初始化 SW 渲染器...");
        Console.WriteLine($"[SW翻译] 构建版本: {SwTranslationBuild}");
        EnsureIconConfigFile();
        await EnsureBrowserReadyAsync(forceRestart: true);
        Console.WriteLine("SW 渲染器初始化完成");
    }

    public async Task CloseAsync()
    {
        await _browserLifecycleLock.WaitAsync();
        try
        {
            if (_browser == null)
            {
                return;
            }

            await CloseBrowserNoThrowAsync(_browser);
            _browser = null;
        }
        finally
        {
            _browserLifecycleLock.Release();
        }
    }

    public async Task<Stream> GenerateStatsImageAsync(
        string jsonResponse,
        string? customAvatarSrc = null,
        string? fallbackUuid = null,
        string? queriedPlayerName = null,
        int chipIconSize = 28,
        string? swxpShow = null,
        string? customTitleBadgeHtml = null)
    {
        var root = JObject.Parse(jsonResponse);
        var data = (root["data"] as JObject) ?? root;

        var playerName = ResolvePlayerName(data, queriedPlayerName);
        var isBanned = ReadBoolean(data["banned"]);
        var totals = BuildTotals(data);
        var effects = BuildEffects(data);
        await ApplyAiTranslationsAsync(data, totals, effects);
        var kitName = ResolveKitName(data);
        var avatarUuid = !string.IsNullOrWhiteSpace(fallbackUuid)
            ? fallbackUuid!
            : ResolveUuid(data) ?? string.Empty;
        var avatarSrc = ResolveAvatarSrc(customAvatarSrc, avatarUuid);

        var html = BuildHtml(
            avatarSrc,
            playerName,
            isBanned,
            kitName,
            totals.TotalGames,
            totals.TotalKills,
            totals.TotalDeaths,
            totals.KdRatio,
            totals.TotalWins,
            totals.TotalLosses,
            totals.WinRate,
            totals.ProjectileKills,
            totals.UseItems,
            totals.SpecialItems,
            effects,
            chipIconSize,
            swxpShow,
            customTitleBadgeHtml
        );

        return await RenderStatsCardAsync(html);
    }

    private async Task<Stream> RenderStatsCardAsync(string htmlContent)
    {
        await EnsureBrowserReadyAsync();
        if (_browser == null)
        {
            throw new InvalidOperationException("SW 渲染器未初始化。");
        }

        using var page = await _browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1120, Height = 1240 });
        await page.SetContentAsync(htmlContent);

        var cardElement = await page.QuerySelectorAsync(".glass-card");
        if (cardElement == null)
        {
            throw new InvalidOperationException("SW 卡片渲染失败：未找到卡片节点。");
        }

        var cardWidth = await page.EvaluateFunctionAsync<int>(
            "el => Math.ceil(Math.max(el.getBoundingClientRect().width, el.scrollWidth))",
            cardElement);
        var cardHeight = await page.EvaluateFunctionAsync<int>(
            "el => Math.ceil(Math.max(el.getBoundingClientRect().height, el.scrollHeight))",
            cardElement);
        var targetWidth = Math.Clamp(cardWidth + 120, 1120, 2200);
        var targetHeight = Math.Clamp(cardHeight + 120, 1240, 5000);
        await page.SetViewportAsync(new ViewPortOptions { Width = targetWidth, Height = targetHeight });

        return await cardElement.ScreenshotStreamAsync();
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
                await CloseBrowserNoThrowAsync(_browser);
                _browser = null;
            }

            var edgePath = ResolveEdgeExecutablePath();
            if (string.IsNullOrWhiteSpace(edgePath))
            {
                throw new FileNotFoundException("未找到 Edge 可执行文件。请设置环境变量 EDGE_PATH 或安装 Edge 到默认路径。");
            }

            var profileDir = Path.Combine(AppContext.BaseDirectory, "pw-profiles", "sw");
            Directory.CreateDirectory(profileDir);
            try
            {
                _browser = await LaunchBrowserAsync(edgePath, profileDir);
            }
            catch
            {
                var fallbackProfileDir = Path.Combine(Path.GetTempPath(), "bedwarsbot-pw", $"sw-{Guid.NewGuid():N}");
                Directory.CreateDirectory(fallbackProfileDir);
                _browser = await LaunchBrowserAsync(edgePath, fallbackProfileDir);
            }
        }
        finally
        {
            _browserLifecycleLock.Release();
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

    private static async Task CloseBrowserNoThrowAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync();
        }
        catch
        {
        }

        try
        {
            browser.Dispose();
        }
        catch
        {
        }
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

    private static string ResolvePlayerName(JObject data, string? queriedPlayerName)
    {
        if (!string.IsNullOrWhiteSpace(queriedPlayerName))
        {
            return queriedPlayerName.Trim();
        }

        return data["name"]?.ToString()
               ?? data["playername"]?.ToString()
               ?? data["username"]?.ToString()
               ?? "Unknown";
    }

    private static bool ReadBoolean(JToken? token)
    {
        if (token == null)
        {
            return false;
        }

        return token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Integer => token.Value<int>() != 0,
            _ => bool.TryParse(token.ToString(), out var parsed) && parsed
        };
    }

    private static string? ResolveUuid(JObject data)
    {
        var raw = data["uuid"]?.ToString()
                  ?? data["_id"]?.ToString()
                  ?? string.Empty;
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static string ResolveKitName(JObject data)
    {
        var kit = data.SelectToken("skywars.effect.kit")?.ToString();
        if (string.IsNullOrWhiteSpace(kit))
        {
            return "未知职业";
        }

        return TranslateKitName(kit.Trim());
    }

    private static string TranslateKitName(string rawKit)
    {
        var key = NormalizeKitKey(rawKit);

        if (KitDisplayNameMap.TryGetValue(key, out var translated))
        {
            return translated;
        }

        if (AiKitDisplayNameMap.TryGetValue(key, out translated))
        {
            return translated;
        }

        if (KitDisplayNameMap.TryGetValue(rawKit, out translated))
        {
            return translated;
        }

        if (AiKitDisplayNameMap.TryGetValue(rawKit, out translated))
        {
            return translated;
        }

        return rawKit;
    }

    private static Dictionary<string, string> BuildEffects(JObject data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.SelectToken("skywars.effect") is not JObject effectObj)
        {
            return result;
        }

        foreach (var property in effectObj.Properties())
        {
            var key = NormalizeItemKey(property.Name);
            var rawValue = property.Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            result[key] = TranslateEffectValue(key, rawValue);
        }

        return result;
    }

    private static string TranslateEffectValue(string effectKey, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return "无";
        }

        var normalizedRawValue = NormalizeEffectValueKey(rawValue);

        if (string.Equals(effectKey, "KIT", StringComparison.OrdinalIgnoreCase))
        {
            return TranslateKitName(rawValue);
        }

        if (string.Equals(effectKey, "KILLSOUND", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveMappedValue(KillSoundDisplayNameMap, rawValue, normalizedRawValue, out var killSoundName))
            {
                return killSoundName;
            }

            if (AiEffectValueMap.TryGetValue(BuildEffectValueKey(effectKey, rawValue), out var aiKillSound))
            {
                return aiKillSound;
            }

            return rawValue;
        }

        if (string.Equals(effectKey, "PARTICLEEFFECT", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveMappedValue(ParticleEffectDisplayNameMap, rawValue, normalizedRawValue, out var particleName))
            {
                return particleName;
            }

            if (AiEffectValueMap.TryGetValue(BuildEffectValueKey(effectKey, rawValue), out var aiParticle))
            {
                return aiParticle;
            }

            return rawValue;
        }

        if (AiEffectValueMap.TryGetValue(BuildEffectValueKey(effectKey, rawValue), out var aiValue))
        {
            return aiValue;
        }

        if (TryResolveMappedValue(GenericEffectValueDisplayNameMap, rawValue, normalizedRawValue, out var genericValue))
        {
            return genericValue;
        }

        return rawValue;
    }

    private static bool TryResolveMappedValue(
        IReadOnlyDictionary<string, string> map,
        string rawValue,
        string normalizedRawValue,
        out string translated)
    {
        if (map.TryGetValue(rawValue, out translated!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedRawValue) && map.TryGetValue(normalizedRawValue, out translated!))
        {
            return true;
        }

        translated = string.Empty;
        return false;
    }

    private static string NormalizeKitKey(string rawKit)
    {
        return rawKit
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string BuildEffectValueKey(string effectKey, string rawValue)
    {
        var normalizedEffect = NormalizeItemKey(effectKey);
        var normalizedValue = rawValue.Trim();
        return $"{normalizedEffect}|{normalizedValue}";
    }

    private async Task ApplyAiTranslationsAsync(JObject data, SkywarsTotals totals, Dictionary<string, string> effects)
    {
        await ApplyAiItemNameTranslationsAsync(totals);
        await ApplyAiKitTranslationsAsync(data);
        await ApplyAiEffectNameTranslationsAsync(effects);
        await ApplyAiEffectValueTranslationsAsync(effects);
    }

    private static bool ShouldTranslateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'))
        {
            return false;
        }

        return true;
    }

    private async Task ApplyAiItemNameTranslationsAsync(SkywarsTotals totals)
    {
        var pendingKeys = totals.UseItems.Keys
            .Concat(totals.SpecialItems.Keys)
            .Select(NormalizeItemKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !ItemDisplayNameMap.ContainsKey(x))
            .Where(x => !AiItemDisplayNameMap.ContainsKey(x))
            .Where(ShouldTranslateText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingKeys.Count == 0)
        {
            return;
        }

        var translated = await DeepSeekTranslator.TranslateTermsAsync(pendingKeys, "skywars_item");
        foreach (var term in pendingKeys)
        {
            if (!translated.TryGetValue(term, out var zh) || string.IsNullOrWhiteSpace(zh))
            {
                continue;
            }

            AiItemDisplayNameMap[NormalizeItemKey(term)] = zh.Trim();
        }
    }

    private async Task ApplyAiKitTranslationsAsync(JObject data)
    {
        var rawKit = data.SelectToken("skywars.effect.kit")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(rawKit))
        {
            return;
        }

        var normalizedKit = NormalizeKitKey(rawKit);
        if (KitDisplayNameMap.ContainsKey(normalizedKit) || AiKitDisplayNameMap.ContainsKey(normalizedKit))
        {
            return;
        }

        if (!ShouldTranslateText(rawKit))
        {
            return;
        }

        var translated = await DeepSeekTranslator.TranslateTermsAsync(new[] { rawKit }, "skywars_kit");
        if (!translated.TryGetValue(rawKit, out var zh) || string.IsNullOrWhiteSpace(zh))
        {
            return;
        }

        var safe = zh.Trim();
        AiKitDisplayNameMap[normalizedKit] = safe;
        AiKitDisplayNameMap[rawKit] = safe;
    }

    private async Task ApplyAiEffectValueTranslationsAsync(Dictionary<string, string> effects)
    {
        if (effects.Count == 0)
        {
            return;
        }

        var pending = new List<(string EffectKey, string RawValue)>();
        foreach (var entry in effects)
        {
            var effectKey = NormalizeItemKey(entry.Key);
            var rawValue = entry.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (string.Equals(rawValue, "无", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ShouldTranslateText(rawValue))
            {
                continue;
            }

            if (string.Equals(effectKey, "KILLSOUND", StringComparison.OrdinalIgnoreCase)
                && KillSoundDisplayNameMap.ContainsKey(rawValue))
            {
                continue;
            }

            if (string.Equals(effectKey, "PARTICLEEFFECT", StringComparison.OrdinalIgnoreCase)
                && ParticleEffectDisplayNameMap.ContainsKey(rawValue))
            {
                continue;
            }

            var compositeKey = BuildEffectValueKey(effectKey, rawValue);
            if (AiEffectValueMap.ContainsKey(compositeKey))
            {
                continue;
            }

            pending.Add((effectKey, rawValue));
        }

        if (pending.Count == 0)
        {
            return;
        }

        var uniqueValues = pending
            .Select(x => x.RawValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var translated = await DeepSeekTranslator.TranslateTermsAsync(uniqueValues, "skywars_effect");

        foreach (var term in pending)
        {
            if (!translated.TryGetValue(term.RawValue, out var zh) || string.IsNullOrWhiteSpace(zh))
            {
                continue;
            }

            AiEffectValueMap[BuildEffectValueKey(term.EffectKey, term.RawValue)] = zh.Trim();
        }

        var keys = effects.Keys.ToList();
        foreach (var key in keys)
        {
            var normalizedKey = NormalizeItemKey(key);
            var rawValue = effects[key]?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (AiEffectValueMap.TryGetValue(BuildEffectValueKey(normalizedKey, rawValue), out var aiValue))
            {
                effects[key] = aiValue;
            }
        }
    }

    private async Task ApplyAiEffectNameTranslationsAsync(Dictionary<string, string> effects)
    {
        if (effects.Count == 0)
        {
            return;
        }

        var pendingKeys = effects.Keys
            .Select(NormalizeItemKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !EffectDisplayNameMap.ContainsKey(x))
            .Where(x => !AiEffectDisplayNameMap.ContainsKey(x))
            .Where(ShouldTranslateText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingKeys.Count == 0)
        {
            return;
        }

        var translated = await DeepSeekTranslator.TranslateTermsAsync(pendingKeys, "skywars_effect_key");
        foreach (var key in pendingKeys)
        {
            if (!translated.TryGetValue(key, out var zh) || string.IsNullOrWhiteSpace(zh))
            {
                continue;
            }

            AiEffectDisplayNameMap[key] = zh.Trim();
        }
    }

    private static string BuildSwxpBadgeHtml(string? rawSwxpShow)
    {
        if (string.IsNullOrWhiteSpace(rawSwxpShow))
        {
            return "<div class=\"mc-level-badge\"><span style=\"color:#FFFFFF;text-shadow:0 1px 3px rgba(0,0,0,0.8);\">Lv. ?</span></div>";
        }

        var text = rawSwxpShow.Trim();
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
            sb.Append("<span style=\"color:#FFFFFF;text-shadow:0 1px 3px rgba(0,0,0,0.8);\">Lv. ?</span>");
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

    private static string ResolveAvatarSrc(string? customAvatarSrc, string? fallbackUuid)
    {
        if (!string.IsNullOrWhiteSpace(customAvatarSrc))
        {
            return customAvatarSrc;
        }

        if (!string.IsNullOrWhiteSpace(fallbackUuid))
        {
            var compact = fallbackUuid.Replace("-", string.Empty);
            return $"https://visage.surgeplay.com/bust/512/{compact}";
        }

        return $"https://minotar.net/avatar/{DefaultAvatarName}/100.png";
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

    private static string ResolvePzDirectory()
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

    private string EnsureIconConfigFile()
    {
        var iconDir = ResolvePzDirectory();
        Directory.CreateDirectory(iconDir);
        var configPath = Path.Combine(iconDir, IconsConfigFileName);
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, BuildDefaultIconConfigJson(), Encoding.UTF8);
            return configPath;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonConvert.DeserializeObject<IconConfig>(json) ?? new IconConfig();
            if (EnsureIconConfigKeys(config))
            {
                var merged = new { useItems = config.UseItems, specialItems = config.SpecialItems };
                File.WriteAllText(configPath, JsonConvert.SerializeObject(merged, Formatting.Indented), Encoding.UTF8);
            }
        }
        catch
        {
            File.WriteAllText(configPath, BuildDefaultIconConfigJson(), Encoding.UTF8);
        }

        return configPath;
    }

    private static bool EnsureIconConfigKeys(IconConfig config)
    {
        config.UseItems ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.SpecialItems ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var key in DefaultUseItemsOrder)
        {
            if (config.UseItems.ContainsKey(key))
            {
                continue;
            }

            config.UseItems[key] = string.Empty;
            changed = true;
        }

        foreach (var key in DefaultSpecialItemsOrder)
        {
            if (config.SpecialItems.ContainsKey(key))
            {
                continue;
            }

            config.SpecialItems[key] = string.Empty;
            changed = true;
        }

        return changed;
    }

    private static string BuildDefaultIconConfigJson()
    {
        var useItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DefaultUseItemsOrder)
        {
            useItems[key] = string.Empty;
        }

        var specialItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DefaultSpecialItemsOrder)
        {
            specialItems[key] = string.Empty;
        }

        var payload = new
        {
            useItems,
            specialItems
        };

        return JsonConvert.SerializeObject(payload, Formatting.Indented);
    }

    private IconConfig GetIconConfig()
    {
        var configPath = EnsureIconConfigFile();
        if (!File.Exists(configPath))
        {
            return new IconConfig();
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(configPath);
        if (_iconConfig != null && lastWriteUtc <= _iconConfigLastWriteUtc)
        {
            return _iconConfig;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonConvert.DeserializeObject<IconConfig>(json) ?? new IconConfig();
            if (EnsureIconConfigKeys(config))
            {
                var merged = new { useItems = config.UseItems, specialItems = config.SpecialItems };
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
            ? $"[SW模板] 已自动修复/重建: {htmlPath}"
            : $"[SW模板] 使用现有模板: {htmlPath}");
        return htmlPath;
    }

    private static bool IsLegacyOrBrokenTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return true;
        if (!template.Contains("{{itemUsageChipsHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{effectsChipsHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{specialItemsChipsHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{customTitleBadgeHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{swxpBadgeHtml}}", StringComparison.Ordinal)) return true;
        if (!template.Contains("{{chipIconSize}}", StringComparison.Ordinal)) return true;
        if (!template.Contains(".resources-panel", StringComparison.Ordinal)) return true;
        if (!template.Contains(".player-id", StringComparison.Ordinal)) return true;
        if (!template.Contains(".chips-grid", StringComparison.Ordinal)) return true;
        if (!template.Contains("class=\"glass-card\"", StringComparison.Ordinal)) return true;
        if (template.Contains("?/span", StringComparison.Ordinal)) return true;
        if (template.Contains("甯", StringComparison.Ordinal) || template.Contains("鎴", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string GetBuiltinHtmlTemplate()
    {
        return """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Skywars Stats - Soft UI</title>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@500;700;900&family=Nunito:wght@700;800;900&display=swap');
        {{customFontFaceCss}}
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: {{globalFontFamily}};
            background: #C287E8;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            padding: 30px;
        }
        .glass-card {
            --chip-icon-size: {{chipIconSize}}px;
            width: 1000px;
            background: rgba(255, 255, 255, 0.65);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid rgba(255, 255, 255, 0.6);
            border-radius: 30px;
            box-shadow: 0 20px 50px rgba(108, 99, 255, 0.15), inset 0 2px 5px rgba(255, 255, 255, 0.8);
            overflow: hidden;
            color: #334155;
        }
        .header {
            padding: 40px 50px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-bottom: 1px solid rgba(255, 255, 255, 0.5);
            background: linear-gradient(180deg, rgba(255, 255, 255, 0.4) 0%, rgba(255, 255, 255, 0.1) 100%);
            gap: 24px;
        }
        .user-profile { display: flex; align-items: center; gap: 25px; min-width: 0; }
        .avatar-box { width: 96px; height: 96px; border-radius: 24px; background: #fff; padding: 4px; box-shadow: 0 10px 25px rgba(108, 99, 255, 0.2); flex: 0 0 auto; }
        .avatar-box img { width: 100%; height: 100%; border-radius: 20px; object-fit: cover; }
        .user-info-top { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-top: 8px; margin-bottom: 8px; }
        .kit-badge {
            background: #EEB902;
            color: #1f2937;
            padding: 6px 16px;
            border-radius: 50px;
            font-size: 14px;
            font-weight: 800;
            letter-spacing: 0.5px;
            box-shadow: 0 4px 12px rgba(238, 185, 2, 0.35);
            display: flex;
            align-items: center;
            gap: 6px;
            white-space: nowrap;
        }
        .custom-title-badge { display: inline-block; font-size: 26px; font-weight: 900; line-height: 1; letter-spacing: 0.5px; text-shadow: 0 1px 1px rgba(0, 0, 0, 0.18); }
        .mc-level-badge { display: inline-flex; align-items: center; background: rgba(15, 23, 42, 0.85); padding: 4px 12px; border-radius: 10px; font-family: {{globalFontFamily}}; font-weight: 900; font-size: 17px; letter-spacing: 1px; box-shadow: inset 0 1px 1px rgba(255, 255, 255, 0.2), 0 4px 10px rgba(0, 0, 0, 0.15); border: 1px solid rgba(255, 255, 255, 0.1); transform: translateY(-2px); }
        .player-id { font-size: 40px; font-weight: 900; line-height: 1.1; background: linear-gradient(135deg, #1e293b, #475569); -webkit-background-clip: text; -webkit-text-fill-color: transparent; overflow-wrap: anywhere; }
        .status-badge { display: inline-block; padding: 6px 16px; border-radius: 10px; font-size: 13px; font-weight: 800; }
        .total-games-panel { background: linear-gradient(135deg, #ffffff 0%, #f8fafc 100%); padding: 15px 35px; border-radius: 24px; text-align: center; box-shadow: 0 10px 30px rgba(108, 99, 255, 0.1), inset 0 2px 4px rgba(255, 255, 255, 1); border: 1px solid rgba(255, 255, 255, 0.8); flex: 0 0 auto; }
        .total-label { font-size: 13px; font-weight: 900; color: #8b5cf6; letter-spacing: 1.5px; margin-bottom: 4px; }
        .total-val { font-size: 46px; font-weight: 900; line-height: 1; color: #3b82f6; }
        .stats-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); padding: 40px 50px; gap: 30px; border-bottom: 1px solid rgba(255, 255, 255, 0.5); }
        .stat-card { background: rgba(255, 255, 255, 0.5); border-radius: 24px; padding: 30px 20px; text-align: center; border: 1px solid rgba(255, 255, 255, 0.6); box-shadow: 0 10px 20px rgba(0, 0, 0, 0.02); min-width: 0; }
        .stat-title { font-size: 16px; font-weight: 900; color: #64748b; letter-spacing: 1px; margin-bottom: 20px; text-transform: uppercase; }
        .main-ratio { font-size: 58px; font-weight: 900; line-height: 1; margin-bottom: 5px; background: linear-gradient(135deg, #6366f1, #a855f7); -webkit-background-clip: text; -webkit-text-fill-color: transparent; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .ratio-desc { font-size: 15px; font-weight: 800; color: #94a3b8; margin-bottom: 25px; }
        .sub-stats { display: flex; justify-content: space-around; background: rgba(255, 255, 255, 0.6); border-radius: 16px; padding: 15px 10px; }
        .sub-box { display: flex; flex-direction: column; gap: 4px; }
        .sub-val { font-size: 22px; font-weight: 900; color: #1e293b; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .sub-label { font-size: 14px; font-weight: 700; color: #64748b; }
        .resources-panel { padding: 40px 50px 50px; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 24px; }
        .res-column { min-width: 0; }
        .res-header { font-size: 18px; font-weight: 900; color: #475569; margin-bottom: 20px; display: flex; align-items: center; gap: 10px; }
        .res-header::before { content: ''; width: 6px; height: 18px; background: #8b5cf6; border-radius: 4px; }
        .chips-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 15px; }
        .effects-grid { grid-template-columns: 1fr; }
        .chip { background: rgba(255, 255, 255, 0.8); border-radius: 14px; padding: 12px 18px; display: flex; justify-content: space-between; align-items: center; border: 1px solid rgba(255, 255, 255, 0.9); box-shadow: 0 4px 10px rgba(0, 0, 0, 0.02); font-size: 15px; font-weight: 800; color: #334155; gap: 8px; min-width: 0; }
        .chip-left { display: inline-flex; align-items: center; min-width: 0; gap: 9px; flex: 1 1 auto; }
        .chip-icon { width: var(--chip-icon-size); height: var(--chip-icon-size); max-width: var(--chip-icon-size); max-height: var(--chip-icon-size); object-fit: contain; flex: 0 0 var(--chip-icon-size); display: block; }
        .chip-name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; max-width: 100%; }
        .chip-val { color: #6366f1; font-weight: 900; font-size: 17px; flex: 0 0 auto; font-family: {{globalFontFamily}}; }
    </style>
</head>
<body>
    <div class="glass-card">
        <div class="header">
            <div class="user-profile">
                <div class="avatar-box"><img src="{{avatarSrc}}" alt="Avatar" onerror="this.src='https://minotar.net/avatar/Steve/100.png'"></div>
                <div>
                    <div class="kit-badge">常用职业: {{kitName}}</div>
                    <div class="user-info-top">{{customTitleBadgeHtml}}{{swxpBadgeHtml}}</div>
                    <div class="player-id">{{name}}</div>
                    <div class="status-badge" style="{{statusStyle}}">{{statusText}}</div>
                </div>
            </div>
            <div class="total-games-panel"><div class="total-label">总场次 TOTAL GAMES</div><div class="total-val">{{totalGames}}</div></div>
        </div>
        <div class="stats-grid">
            <div class="stat-card">
                <div class="stat-title">战斗表现 COMBAT</div>
                <div class="main-ratio" style="background: linear-gradient(135deg, #f43f5e, #fb7185); -webkit-background-clip: text;">{{kdRatio}}</div>
                <div class="ratio-desc">K/D RATIO</div>
                <div class="sub-stats"><div class="sub-box"><span class="sub-val">{{totalKills}}</span><span class="sub-label">总击杀</span></div><div class="sub-box"><span class="sub-val">{{totalDeaths}}</span><span class="sub-label">总死亡</span></div></div>
            </div>
            <div class="stat-card">
                <div class="stat-title">胜负表现 SESSION</div>
                <div class="main-ratio" style="background: linear-gradient(135deg, #10b981, #34d399); -webkit-background-clip: text;">{{winRate}}%</div>
                <div class="ratio-desc">WIN RATE</div>
                <div class="sub-stats"><div class="sub-box"><span class="sub-val">{{wins}}</span><span class="sub-label">总胜场</span></div><div class="sub-box"><span class="sub-val">{{losses}}</span><span class="sub-label">总败场</span></div></div>
            </div>
            <div class="stat-card">
                <div class="stat-title">特殊击杀 SPECIAL</div>
                <div class="main-ratio" style="background: linear-gradient(135deg, #f59e0b, #fbbf24); -webkit-background-clip: text;">{{projectileKills}}</div>
                <div class="ratio-desc">PROJECTILE KILLS</div>
                <div class="sub-stats"><div class="sub-box" style="width: 100%;"><span class="sub-val">Archery / Snowballs</span><span class="sub-label">抛射物击杀</span></div></div>
            </div>
        </div>
        <div class="resources-panel">
            <div class="res-column"><div class="res-header">局内道具使用 ITEM USAGE</div><div class="chips-grid">{{itemUsageChipsHtml}}</div></div>
            <div class="res-column"><div class="res-header">特效 EFFECTS</div><div class="chips-grid effects-grid">{{effectsChipsHtml}}</div></div>
            <div class="res-column"><div class="res-header">局内获取次数 ACQUIRED</div><div class="chips-grid">{{specialItemsChipsHtml}}</div></div>
        </div>
    </div>
</body>
</html>
""";
    }

    private string GetHtmlTemplateText()
    {
        var templatePath = EnsureHtmlTemplateFile();

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

    private sealed class SkywarsTotals
    {
        public int TotalGames { get; set; }
        public int TotalKills { get; set; }
        public int TotalWins { get; set; }
        public int TotalLosses { get; set; }
        public int TotalDeaths { get; set; }
        public int ProjectileKills { get; set; }
        public double KdRatio { get; set; }
        public double WinRate { get; set; }
        public Dictionary<string, int> UseItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SpecialItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static SkywarsTotals BuildTotals(JObject data)
    {
        var totals = new SkywarsTotals
        {
            TotalGames = data.Value<int?>("total_game") ?? 0,
            TotalKills = data.Value<int?>("total_kills") ?? 0,
            TotalWins = data.Value<int?>("total_win") ?? 0
        };

        var modeGames = 0;
        var modeWins = 0;
        var modeLoses = 0;
        var modeKills = 0;
        var modeDeaths = 0;
        var modeProjectileKills = 0;
        var useItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var specialItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (data["skywars"] is JObject skywars)
        {
            foreach (var mode in skywars.Properties())
            {
                if (string.Equals(mode.Name, "effect", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mode.Value is not JObject modeObj)
                {
                    continue;
                }

                modeGames += modeObj.Value<int?>("game") ?? 0;
                modeWins += modeObj.Value<int?>("win") ?? 0;
                modeLoses += modeObj.Value<int?>("lose") ?? 0;
                modeKills += modeObj.Value<int?>("kills") ?? 0;
                modeDeaths += modeObj.Value<int?>("deaths") ?? 0;
                modeProjectileKills += modeObj.Value<int?>("projectileKills")
                                      ?? modeObj.Value<int?>("projectile_kills")
                                      ?? 0;

                MergeCounter(modeObj["use_item"] as JObject, useItems);
                MergeCounter(modeObj["special_item"] as JObject, specialItems);
            }
        }

        if (totals.TotalGames <= 0) totals.TotalGames = modeGames;
        if (totals.TotalKills <= 0) totals.TotalKills = modeKills;
        if (totals.TotalWins <= 0) totals.TotalWins = modeWins;

        totals.TotalDeaths = modeDeaths;
        totals.ProjectileKills = modeProjectileKills;
        totals.UseItems = useItems;
        totals.SpecialItems = specialItems;

        var lossesFromTotal = totals.TotalGames - totals.TotalWins;
        totals.TotalLosses = lossesFromTotal > 0 ? lossesFromTotal : Math.Max(modeLoses, 0);
        if (totals.TotalDeaths <= 0)
        {
            totals.TotalDeaths = Math.Max(totals.TotalLosses, 1);
        }

        totals.KdRatio = totals.TotalDeaths == 0
            ? totals.TotalKills
            : (double)totals.TotalKills / totals.TotalDeaths;
        totals.WinRate = totals.TotalGames == 0
            ? 0
            : (double)totals.TotalWins / totals.TotalGames * 100;
        return totals;
    }

    private static void MergeCounter(JObject? source, Dictionary<string, int> target)
    {
        if (source == null)
        {
            return;
        }

        foreach (var property in source.Properties())
        {
            if (!int.TryParse(property.Value.ToString(), out var value))
            {
                continue;
            }

            if (target.TryGetValue(property.Name, out var existing))
            {
                target[property.Name] = existing + value;
            }
            else
            {
                target[property.Name] = value;
            }
        }
    }

    private static string NormalizeItemKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return string.Empty;
        }

        return rawKey.Trim()
            .Replace(" ", "_", StringComparison.Ordinal)
            .Replace("-", "_", StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string? ResolveIconSrc(IconConfig config, Dictionary<string, string> iconMap, string key)
    {
        if (!iconMap.TryGetValue(key, out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var trimmed = rawPath.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith('.'))
        {
            var fullPath = Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(config.BaseDirectory, trimmed));
            if (!File.Exists(fullPath))
            {
                return null;
            }

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
        if (!File.Exists(fallbackPath))
        {
            return null;
        }

        return BuildDataUriFromFile(fallbackPath);
    }

    private static string? BuildDataUriFromFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0)
            {
                return null;
            }

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

    private string GenerateChipsHtml(
        Dictionary<string, int> source,
        IReadOnlyList<string> orderedKeys,
        IconConfig iconConfig,
        Dictionary<string, string> iconMap)
    {
        var normalizedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source)
        {
            var key = NormalizeItemKey(entry.Key);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (normalizedCounts.TryGetValue(key, out var existing))
            {
                normalizedCounts[key] = existing + entry.Value;
            }
            else
            {
                normalizedCounts[key] = entry.Value;
            }
        }

        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AppendChip(string key, int value)
        {
            var displayName = ItemDisplayNameMap.TryGetValue(key, out var mapped)
                ? mapped
                : (AiItemDisplayNameMap.TryGetValue(key, out var aiMapped)
                    ? aiMapped
                    : key.Replace("_", " ", StringComparison.Ordinal));
            displayName = WebUtility.HtmlEncode(displayName);

            var iconSrc = ResolveIconSrc(iconConfig, iconMap, key);
            var hasIcon = !string.IsNullOrWhiteSpace(iconSrc);
            var iconHtml = hasIcon
                ? $"<img class='chip-icon' src='{iconSrc}' alt='' onerror=\"this.style.display='none';this.removeAttribute('src');\">"
                : string.Empty;
            var nameHtml = hasIcon
                ? string.Empty
                : $"<span class='chip-name'>{displayName}</span>";

            sb.Append($@"
                <div class='chip'>
                    <span class='chip-left'>{iconHtml}{nameHtml}</span>
                    <span class='chip-val'>{value.ToString("N0", CultureInfo.CurrentCulture)}</span>
                </div>");
        }

        foreach (var rawKey in orderedKeys)
        {
            var key = NormalizeItemKey(rawKey);
            if (!seen.Add(key))
            {
                continue;
            }

            normalizedCounts.TryGetValue(key, out var value);
            AppendChip(key, value);
        }

        foreach (var entry in normalizedCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = NormalizeItemKey(entry.Key);
            if (!seen.Add(key))
            {
                continue;
            }

            AppendChip(key, entry.Value);
        }

        return sb.ToString();
    }

    private static string GenerateEffectsChipsHtml(Dictionary<string, string> effects)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AppendChip(string key, string value)
        {
            var displayName = ResolveEffectDisplayName(key);
            var displayValue = ResolveEffectDisplayValue(key, value);

            sb.Append($@"
                <div class='chip'>
                    <span class='chip-left'><span class='chip-name'>{WebUtility.HtmlEncode(displayName)}</span></span>
                    <span class='chip-val'>{WebUtility.HtmlEncode(displayValue)}</span>
                </div>");
        }

        foreach (var rawKey in DefaultEffectOrder)
        {
            var key = NormalizeItemKey(rawKey);
            if (!seen.Add(key))
            {
                continue;
            }

            effects.TryGetValue(key, out var value);
            AppendChip(key, string.IsNullOrWhiteSpace(value) ? "无" : value);
        }

        foreach (var entry in effects.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = NormalizeItemKey(entry.Key);
            if (!seen.Add(key))
            {
                continue;
            }

            AppendChip(key, string.IsNullOrWhiteSpace(entry.Value) ? "无" : entry.Value);
        }

        return sb.ToString();
    }

    private static string ResolveEffectDisplayName(string rawKey)
    {
        var key = NormalizeItemKey(rawKey);
        if (EffectDisplayNameMap.TryGetValue(key, out var mapped))
        {
            return mapped;
        }

        if (AiEffectDisplayNameMap.TryGetValue(key, out var aiMapped))
        {
            return aiMapped;
        }

        if (key.StartsWith("WIN", StringComparison.OrdinalIgnoreCase))
        {
            return "胜利音效";
        }

        if (key.Contains("KILL", StringComparison.OrdinalIgnoreCase) && key.Contains("SOUND", StringComparison.OrdinalIgnoreCase))
        {
            return "击杀音效";
        }

        if (key.Contains("PARTICLE", StringComparison.OrdinalIgnoreCase))
        {
            return "粒子特效";
        }

        if (key.Contains("PROJECTILE", StringComparison.OrdinalIgnoreCase))
        {
            return "抛射物特效";
        }

        if (key.Contains("GLASS", StringComparison.OrdinalIgnoreCase) && key.Contains("COLOR", StringComparison.OrdinalIgnoreCase))
        {
            return "玻璃颜色";
        }

        if (key.Contains("KIT", StringComparison.OrdinalIgnoreCase))
        {
            return "职业";
        }

        return key;
    }

    private static string ResolveEffectDisplayValue(string rawKey, string rawValue)
    {
        var normalizedKey = NormalizeItemKey(rawKey);
        var normalizedValue = rawValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return "无";
        }

        var mapped = TranslateEffectValue(normalizedKey, normalizedValue);
        if (!string.Equals(mapped, normalizedValue, StringComparison.Ordinal))
        {
            return mapped;
        }

        var loose = NormalizeEffectValueKey(normalizedValue);
        return loose switch
        {
            "none" => "无",
            "green" => "绿色",
            "lightgray" => "浅灰色",
            "happy" => "开心粒子",
            "critical" => "暴击粒子",
            "menu" => "菜单音效",
            "ghasthurt" => "恶魂受伤音效",
            "zombiehorsedeath" => "僵尸马死亡音效",
            "enderdragondeath" => "末影龙死亡音效",
            _ => normalizedValue
        };
    }

    private static string NormalizeEffectValueKey(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        return rawValue.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private string BuildHtml(
        string avatarSrc,
        string playerName,
        bool isBanned,
        string kitName,
        int totalGames,
        int totalKills,
        int totalDeaths,
        double kdRatio,
        int wins,
        int losses,
        double winRate,
        int projectileKills,
        Dictionary<string, int> useItems,
        Dictionary<string, int> specialItems,
        Dictionary<string, string> effects,
        int chipIconSize,
        string? swxpShow,
        string? customTitleBadgeHtml)
    {
        var template = GetHtmlTemplateText();
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();
        var iconConfig = GetIconConfig();
        var iconSizeValue = Math.Clamp(chipIconSize, 16, 40);
        var itemUsageChipsHtml = GenerateChipsHtml(useItems, DefaultUseItemsOrder, iconConfig, iconConfig.UseItemsNormalized);
        var effectsChipsHtml = GenerateEffectsChipsHtml(effects);
        var specialItemsChipsHtml = GenerateChipsHtml(specialItems, DefaultSpecialItemsOrder, iconConfig, iconConfig.SpecialItemsNormalized);
        var swxpBadgeHtml = BuildSwxpBadgeHtml(swxpShow);
        var safeName = WebUtility.HtmlEncode(playerName);
        var safeKit = WebUtility.HtmlEncode(kitName);
        var statusText = isBanned ? "🔴 账号状态封禁" : "🟢 账号状态正常";
        var statusStyle = isBanned
            ? "background: rgba(239,68,68,0.12); color:#dc2626; border:1px solid rgba(239,68,68,0.24);"
            : "background: rgba(16,185,129,0.1); color:#059669; border:1px solid rgba(16,185,129,0.2);";

        return template
            .Replace("{{customFontFaceCss}}", customFontFaceCss)
            .Replace("{{globalFontFamily}}", globalFontFamily)
            .Replace("{{chipIconSize}}", iconSizeValue.ToString(CultureInfo.InvariantCulture))
            .Replace("{{avatarSrc}}", avatarSrc)
            .Replace("{{kitName}}", safeKit)
            .Replace("{{customTitleBadgeHtml}}", customTitleBadgeHtml ?? string.Empty)
            .Replace("{{swxpBadgeHtml}}", swxpBadgeHtml)
            .Replace("{{name}}", safeName)
            .Replace("{{statusText}}", statusText)
            .Replace("{{statusStyle}}", statusStyle)
            .Replace("{{totalGames}}", totalGames.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{kdRatio}}", kdRatio.ToString("F2", CultureInfo.CurrentCulture))
            .Replace("{{totalKills}}", totalKills.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{totalDeaths}}", totalDeaths.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{winRate}}", winRate.ToString("F1", CultureInfo.CurrentCulture))
            .Replace("{{wins}}", wins.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{losses}}", losses.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{projectileKills}}", projectileKills.ToString("N0", CultureInfo.CurrentCulture))
            .Replace("{{itemUsageChipsHtml}}", itemUsageChipsHtml)
            .Replace("{{effectsChipsHtml}}", effectsChipsHtml)
            .Replace("{{specialItemsChipsHtml}}", specialItemsChipsHtml);
    }
}
