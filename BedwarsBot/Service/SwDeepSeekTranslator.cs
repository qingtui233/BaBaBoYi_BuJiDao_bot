using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public sealed class SwDeepSeekTranslator
{
    private const string ApiUrl = "https://api.deepseek.com/chat/completions";
    private const string ApiKey = "sk-23056eb740b34f86ad2ab3b562bbae4b";
    private const string ModelName = "deepseek-chat";
    private const string CacheDirectoryName = "pz";
    private const string CacheFileName = "sw-ai-translate-cache.json";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly object CacheIoLock = new();
    private static readonly string CacheFilePath = ResolveCacheFilePath();

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    private static readonly Dictionary<string, string> ForcedGlossary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TNT"] = "\u0054\u004E\u0054\u70B8\u836F",
        ["GOLDEN_APPLE"] = "\u666E\u901A\u91D1\u82F9\u679C",
        ["ENCHANTED_GOLDEN_APPLE"] = "\u9644\u9B54\u91D1\u82F9\u679C",
        ["END_CRYSTAL"] = "\u672B\u5F71\u6C34\u6676",
        ["TOTEM"] = "\u4E0D\u6B7B\u56FE\u817E",
        ["SLIME_BALL"] = "\u51FB\u9000\u7403",
        ["GLASSCOLOR"] = "\u73BB\u7483\u989C\u8272",
        ["PROJECTILEEFFECT"] = "\u629B\u5C04\u7269\u7279\u6548",
        ["WINSOUND"] = "\u80DC\u5229\u97F3\u6548",
        ["WIN_SOUND"] = "\u80DC\u5229\u97F3\u6548",
        ["WIN_"] = "\u80DC\u5229\u97F3\u6548",
        ["none"] = "\u65E0",
        ["explosion"] = "\u7206\u70B8",
        ["green"] = "\u7EFF\u8272",
        ["yellow"] = "\u9EC4\u8272",
        ["lightgray"] = "\u6D45\u7070\u8272",
        ["light_gray"] = "\u6D45\u7070\u8272",
        ["happy"] = "\u5F00\u5FC3\u7C92\u5B50",
        ["critical"] = "\u66B4\u51FB\u7C92\u5B50",
        ["water"] = "\u6C34\u7C92\u5B50",
        ["clouds"] = "\u4E91\u7C92\u5B50",
        ["thunder"] = "\u96F7\u58F0\u97F3\u6548",
        ["ghastwarn"] = "\u6076\u9B42\u8B66\u544A\u97F3\u6548",
        ["ghasthurt"] = "\u6076\u9B42\u53D7\u4F24\u97F3\u6548",
        ["ghast_hurt"] = "\u6076\u9B42\u53D7\u4F24\u97F3\u6548",
        ["menu"] = "\u83DC\u5355\u97F3\u6548",
        ["zombiehorsedeath"] = "\u50F5\u5C38\u9A6C\u6B7B\u4EA1\u97F3\u6548",
        ["enderdragondeath"] = "\u672B\u5F71\u9F99\u6B7B\u4EA1\u97F3\u6548",
        ["Enderman"] = "\u672B\u5F71\u4EBA",
        ["Skeleton"] = "\u9AB7\u9AC5",
        ["Zombie"] = "\u50F5\u5C38",
        ["Creeper"] = "\u82E6\u529B\u6015",
        ["Blaze"] = "\u70C8\u7130\u4EBA",
        ["Pigman"] = "\u50F5\u5C38\u732A\u7075",
        ["Witch"] = "\u5973\u5DEB",
        ["Farmer"] = "\u519C\u592B",
        ["Knight"] = "\u9A91\u58EB",
        ["Scout"] = "\u65A5\u5019",
        ["Healer"] = "\u6CBB\u7597\u5E08",
        ["Archer"] = "\u5F13\u7BAD\u624B",
        ["Pyro"] = "\u706B\u7130\u672F\u58EB",
        ["Armorer"] = "\u62A4\u7532\u5E08"
    };

    private static readonly Dictionary<string, string> AutoTokenGlossary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tnt"] = "TNT",
        ["golden"] = "\u91D1",
        ["enchanted"] = "\u9644\u9B54",
        ["apple"] = "\u82F9\u679C",
        ["end"] = "\u672B\u5F71",
        ["ender"] = "\u672B\u5F71",
        ["crystal"] = "\u6C34\u6676",
        ["totem"] = "\u56FE\u817E",
        ["slime"] = "\u53F2\u83B1\u59C6",
        ["ball"] = "\u7403",
        ["projectile"] = "\u629B\u5C04\u7269",
        ["effect"] = "\u7279\u6548",
        ["particle"] = "\u7C92\u5B50",
        ["kill"] = "\u51FB\u6740",
        ["sound"] = "\u97F3\u6548",
        ["win"] = "\u80DC\u5229",
        ["glass"] = "\u73BB\u7483",
        ["color"] = "\u989C\u8272",
        ["green"] = "\u7EFF\u8272",
        ["red"] = "\u7EA2\u8272",
        ["blue"] = "\u84DD\u8272",
        ["yellow"] = "\u9EC4\u8272",
        ["purple"] = "\u7D2B\u8272",
        ["pink"] = "\u7C89\u8272",
        ["black"] = "\u9ED1\u8272",
        ["white"] = "\u767D\u8272",
        ["gray"] = "\u7070\u8272",
        ["grey"] = "\u7070\u8272",
        ["light"] = "\u6D45",
        ["dark"] = "\u6DF1",
        ["critical"] = "\u66B4\u51FB",
        ["happy"] = "\u5F00\u5FC3",
        ["menu"] = "\u83DC\u5355",
        ["water"] = "\u6C34",
        ["cloud"] = "\u4E91",
        ["clouds"] = "\u4E91",
        ["thunder"] = "\u96F7\u58F0",
        ["warn"] = "\u8B66\u544A",
        ["warning"] = "\u8B66\u544A",
        ["rain"] = "\u96E8",
        ["snow"] = "\u96EA",
        ["fire"] = "\u706B\u7130",
        ["smoke"] = "\u70DF\u96FE",
        ["ghast"] = "\u6076\u9B42",
        ["zombie"] = "\u50F5\u5C38",
        ["horse"] = "\u9A6C",
        ["dragon"] = "\u9F99",
        ["death"] = "\u6B7B\u4EA1",
        ["hurt"] = "\u53D7\u4F24",
        ["kit"] = "\u804C\u4E1A"
    };

    private static readonly string[] AutoTokenKeysByLengthDesc = AutoTokenGlossary.Keys
        .OrderByDescending(x => x.Length)
        .ToArray();

    public async Task<Dictionary<string, string>> TranslateTermsAsync(IEnumerable<string> terms, string termType)
    {
        EnsureCacheLoaded();

        var unique = terms
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (unique.Count == 0)
        {
            return result;
        }

        var cacheChanged = false;
        foreach (var term in unique)
        {
            if (TryGetForced(term, out var forced))
            {
                result[term] = forced;
                cacheChanged |= SetCacheTerm(termType, term, forced);
                continue;
            }

            if (TryGetValidCached(termType, term, out var cached, out var changed))
            {
                result[term] = cached;
                cacheChanged |= changed;
            }
            else
            {
                cacheChanged |= changed;
            }
        }

        var pending = unique
            .Where(x => !result.ContainsKey(x))
            .Where(ShouldTranslate)
            .ToList();

        if (pending.Count > 0)
        {
            try
            {
                var translated = await RequestDeepSeekAsync(pending, termType);
                foreach (var term in pending)
                {
                    if (!translated.TryGetValue(term, out var value) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var clean = value.Trim();
                    if (!IsAcceptableTranslation(term, clean))
                    {
                        continue;
                    }

                    result[term] = clean;
                    cacheChanged |= SetCacheTerm(termType, term, clean);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SW-AI] DeepSeek translation exception: {ex.Message}");
            }

            foreach (var term in pending)
            {
                if (result.ContainsKey(term))
                {
                    continue;
                }

                if (!TryAutoTranslateTerm(termType, term, out var autoTranslated))
                {
                    continue;
                }

                result[term] = autoTranslated;
                cacheChanged |= SetCacheTerm(termType, term, autoTranslated);
            }
        }

        if (cacheChanged)
        {
            SaveCacheNoThrow();
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> RequestDeepSeekAsync(List<string> terms, string termType)
    {
        var body = new
        {
            model = ModelName,
            temperature = 0.0,
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(termType) },
                new { role = "user", content = BuildUserPrompt(terms) }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[SW-AI] DeepSeek request failed: status={(int)response.StatusCode}, body={content}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var root = JObject.Parse(content);
        var text = root.SelectToken("choices[0].message.content")?.ToString();
        var jsonText = ExtractJsonObject(text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var mapObj = JObject.Parse(jsonText);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in mapObj.Properties())
        {
            var key = property.Name?.Trim();
            var value = property.Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string BuildSystemPrompt(string termType)
    {
        var forcedJson = JsonConvert.SerializeObject(ForcedGlossary, Formatting.None);
        return
            "You are a Minecraft Skywars terminology translator. "
            + "Translate terms into Simplified Chinese only. "
            + "Output must be a JSON object where keys are original terms and values are Chinese translations. "
            + "No explanations, no markdown. "
            + "Strictly prioritize this forced glossary: "
            + forcedJson
            + ". If a term is already Chinese or ambiguous, keep the original term. "
            + $"Current term type: {termType}.";
    }

    private static string BuildUserPrompt(List<string> terms)
    {
        var input = JsonConvert.SerializeObject(terms);
        return $"Translate this term array and return a JSON object: {input}";
    }

    private static bool TryGetForced(string term, out string translated)
    {
        if (ForcedGlossary.TryGetValue(term, out translated!))
        {
            return true;
        }

        var normalized = NormalizeLooseTerm(term);
        return ForcedGlossary.TryGetValue(normalized, out translated!);
    }

    private static bool ShouldTranslate(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        return !term.Any(IsCjkChar);
    }

    private static bool IsAcceptableTranslation(string sourceTerm, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        var source = sourceTerm.Trim();
        var target = translated.Trim();
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ShouldTranslate(source) && !target.Any(IsCjkChar))
        {
            return false;
        }

        return true;
    }

    private static bool IsCjkChar(char c)
    {
        return c >= '\u4e00' && c <= '\u9fff';
    }

    private static string BuildCacheKey(string termType, string term)
    {
        return $"{termType}|{term}";
    }

    private static string BuildLooseCacheKey(string termType, string term)
    {
        return $"{termType}|{NormalizeLooseTerm(term)}";
    }

    private static string ParseTermFromCacheKey(string cacheKey)
    {
        var idx = cacheKey.IndexOf('|');
        return idx >= 0 && idx < cacheKey.Length - 1
            ? cacheKey[(idx + 1)..]
            : cacheKey;
    }

    private bool TryGetValidCached(string termType, string term, out string translated, out bool cacheChanged)
    {
        translated = string.Empty;
        cacheChanged = false;

        var exactKey = BuildCacheKey(termType, term);
        if (TryReadAndValidateCacheEntry(exactKey, term, out translated, out var changedExact))
        {
            cacheChanged |= changedExact;
            return true;
        }

        cacheChanged |= changedExact;

        var looseKey = BuildLooseCacheKey(termType, term);
        if (TryReadAndValidateCacheEntry(looseKey, term, out translated, out var changedLoose))
        {
            cacheChanged |= changedLoose;
            cacheChanged |= SetCacheValue(exactKey, translated);
            return true;
        }

        cacheChanged |= changedLoose;
        return false;
    }

    private bool TryReadAndValidateCacheEntry(string cacheKey, string sourceTerm, out string translated, out bool cacheChanged)
    {
        translated = string.Empty;
        cacheChanged = false;

        if (!_cache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        if (!IsAcceptableTranslation(sourceTerm, cached))
        {
            _cache.TryRemove(cacheKey, out _);
            cacheChanged = true;
            return false;
        }

        translated = cached;
        return true;
    }

    private bool SetCacheTerm(string termType, string term, string translated)
    {
        var changed = SetCacheValue(BuildCacheKey(termType, term), translated);
        changed |= SetCacheValue(BuildLooseCacheKey(termType, term), translated);
        return changed;
    }

    private bool SetCacheValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (_cache.TryGetValue(key, out var existing) && string.Equals(existing, value, StringComparison.Ordinal))
        {
            return false;
        }

        _cache[key] = value;
        return true;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        lock (CacheIoLock)
        {
            if (_cacheLoaded)
            {
                return;
            }

            var cacheChanged = false;

            try
            {
                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(CacheFilePath))
                {
                    var json = File.ReadAllText(CacheFilePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var obj = JObject.Parse(json);
                        foreach (var property in obj.Properties())
                        {
                            var key = property.Name?.Trim();
                            var value = property.Value?.ToString()?.Trim();
                            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                            {
                                continue;
                            }

                            var sourceTerm = ParseTermFromCacheKey(key);
                            if (!IsAcceptableTranslation(sourceTerm, value))
                            {
                                cacheChanged = true;
                                continue;
                            }

                            _cache[key] = value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SW-AI] cache load failed: {ex.Message}");
            }
            finally
            {
                _cacheLoaded = true;
            }

            if (cacheChanged)
            {
                SaveCacheNoThrow();
            }
        }
    }

    private void SaveCacheNoThrow()
    {
        lock (CacheIoLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var snapshot = _cache
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(CacheFilePath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SW-AI] cache save failed: {ex.Message}");
            }
        }
    }

    private static string NormalizeLooseTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(term.Length);
        foreach (var ch in term.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static bool TryAutoTranslateTerm(string termType, string term, out string translated)
    {
        translated = string.Empty;

        var normalized = NormalizeLooseTerm(term);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (AutoTokenGlossary.TryGetValue(normalized, out var exact))
        {
            translated = PostProcessAutoTranslation(termType, normalized, exact);
            return !string.IsNullOrWhiteSpace(translated);
        }

        if (!TryTokenize(normalized, out var tokens) || tokens.Count == 0)
        {
            return false;
        }

        var parts = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!AutoTokenGlossary.TryGetValue(token, out var zh))
            {
                return false;
            }

            parts.Add(zh);
        }

        var joined = string.Concat(parts);
        translated = PostProcessAutoTranslation(termType, normalized, joined);
        return !string.IsNullOrWhiteSpace(translated);
    }

    private static string PostProcessAutoTranslation(string termType, string normalized, string translated)
    {
        if (string.Equals(termType, "skywars_effect", StringComparison.OrdinalIgnoreCase))
        {
            if ((normalized.Contains("sound", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("warn", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("thunder", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("death", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("hurt", StringComparison.OrdinalIgnoreCase))
                && !translated.EndsWith("\u97F3\u6548", StringComparison.Ordinal))
            {
                return translated + "\u97F3\u6548";
            }

            if ((normalized.Contains("particle", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("water", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("cloud", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("smoke", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("critical", StringComparison.OrdinalIgnoreCase)
                 || normalized.Contains("happy", StringComparison.OrdinalIgnoreCase))
                && !translated.EndsWith("\u7C92\u5B50", StringComparison.Ordinal))
            {
                return translated + "\u7C92\u5B50";
            }
        }

        if (string.Equals(termType, "skywars_effect_key", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains("sound", StringComparison.OrdinalIgnoreCase) && !translated.EndsWith("\u97F3\u6548", StringComparison.Ordinal))
            {
                return translated + "\u97F3\u6548";
            }

            if (normalized.Contains("effect", StringComparison.OrdinalIgnoreCase) && !translated.EndsWith("\u7279\u6548", StringComparison.Ordinal))
            {
                return translated + "\u7279\u6548";
            }

            if (normalized.Contains("color", StringComparison.OrdinalIgnoreCase) && !translated.EndsWith("\u989C\u8272", StringComparison.Ordinal))
            {
                return translated + "\u989C\u8272";
            }
        }

        return translated;
    }

    private static bool TryTokenize(string raw, out List<string> tokens)
    {
        tokens = new List<string>();
        var index = 0;
        while (index < raw.Length)
        {
            var matched = false;
            foreach (var key in AutoTokenKeysByLengthDesc)
            {
                if (index + key.Length > raw.Length)
                {
                    continue;
                }

                if (!raw.AsSpan(index, key.Length).Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tokens.Add(key);
                index += key.Length;
                matched = true;
                break;
            }

            if (!matched)
            {
                return false;
            }
        }

        return tokens.Count > 0;
    }

    private static string ResolveCacheFilePath()
    {
        var root = ResolveRootDirectory();
        return Path.Combine(root, CacheDirectoryName, CacheFileName);
    }

    private static string ResolveRootDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? firstProjectDir = null;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            if (firstProjectDir == null && File.Exists(Path.Combine(dir.FullName, "BedwarsBot.csproj")))
            {
                firstProjectDir = dir;
            }

            dir = dir.Parent;
        }

        if (firstProjectDir?.Parent != null)
        {
            return firstProjectDir.Parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ExtractJsonObject(string text)
    {
        var raw = text.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = raw.IndexOf('{');
            var lastBrace = raw.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return raw[firstBrace..(lastBrace + 1)];
            }
        }

        if (raw.StartsWith("{", StringComparison.Ordinal) && raw.EndsWith("}", StringComparison.Ordinal))
        {
            return raw;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return raw[start..(end + 1)];
        }

        return string.Empty;
    }
}
