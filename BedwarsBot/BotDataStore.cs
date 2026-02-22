using System.Globalization;
using Newtonsoft.Json;

namespace BedwarsBot;

public sealed class BotDataStore
{
    private readonly object _lock = new();
    private readonly string _qqBindingPath;
    private readonly string _skinBindingPath;
    private readonly string _backgroundBindingPath;
    private readonly string _backgroundSettingsPath;
    private readonly string _bwMessageConfigPath;
    private readonly string _customTitlePath;
    private readonly string _queryableIdsPath;
    private readonly string _napcatUsagePath;

    private Dictionary<string, QqBinding> _qqBindings = new(StringComparer.Ordinal);
    private Dictionary<string, SkinBinding> _skinBindings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BackgroundBinding> _backgroundBindings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CustomTitleBinding> _customTitles = new(StringComparer.OrdinalIgnoreCase);
    private double _backgroundOpacity = DefaultBackgroundOpacity;
    private int _chipIconSize = DefaultChipIconSize;
    private int _playerIdFontSize = DefaultPlayerIdFontSize;
    private string _backgroundSolidColor = string.Empty;
    private string _bwImageCaption = DefaultBwImageCaption;
    private HashSet<string> _queryableIds = new(StringComparer.OrdinalIgnoreCase);
    private string _usageDate = DateTime.Now.ToString("yyyy-MM-dd");
    private int _usageCount;
    private string _lastReportedDate = string.Empty;

    public string RootDirectory { get; }
    public string ConfigDirectory { get; }
    public string AvatarDirectory { get; }
    public string BackgroundDirectory { get; }

    private const double DefaultBackgroundOpacity = 0.6;
    private const int DefaultChipIconSize = 28;
    private const int MinPlayerIdFontSize = 12;
    private const int MaxPlayerIdFontSize = 36;
    private const int DefaultPlayerIdFontSize = 14;
    private const string DefaultBwImageCaption = "想要显示自己的皮肤头像？请输入 !skin add 正版ID 即可使用自己的皮肤头像。";

    public BotDataStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        ConfigDirectory = Path.Combine(rootDirectory, "pz");
        AvatarDirectory = Path.Combine(rootDirectory, "touxiang");
        BackgroundDirectory = Path.Combine(rootDirectory, "background");
        _qqBindingPath = Path.Combine(ConfigDirectory, "qq_bindings.json");
        _skinBindingPath = Path.Combine(ConfigDirectory, "skin_bindings.json");
        _backgroundBindingPath = Path.Combine(ConfigDirectory, "background_bindings.json");
        _backgroundSettingsPath = Path.Combine(ConfigDirectory, "background_settings.json");
        _bwMessageConfigPath = Path.Combine(ConfigDirectory, "bw_message_config.json");
        _customTitlePath = Path.Combine(ConfigDirectory, "custom_titles.json");
        _queryableIdsPath = Path.Combine(ConfigDirectory, "queryable_ids.json");
        _napcatUsagePath = Path.Combine(ConfigDirectory, "napcat_usage_stats.json");
    }

    public void Initialize()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(AvatarDirectory);
            Directory.CreateDirectory(BackgroundDirectory);

            EnsureJsonFile(_qqBindingPath);
            EnsureJsonFile(_skinBindingPath);
            EnsureJsonFile(_backgroundBindingPath);
            EnsureBackgroundSettingsFile(_backgroundSettingsPath);
            EnsureBwMessageConfigFile(_bwMessageConfigPath);
            EnsureJsonFile(_customTitlePath);
            EnsureQueryableIdsFile(_queryableIdsPath);
            EnsureNapcatUsageFile(_napcatUsagePath);

            _qqBindings = LoadJson<Dictionary<string, QqBinding>>(_qqBindingPath) ?? new Dictionary<string, QqBinding>(StringComparer.Ordinal);
            _skinBindings = LoadJson<Dictionary<string, SkinBinding>>(_skinBindingPath) ?? new Dictionary<string, SkinBinding>(StringComparer.OrdinalIgnoreCase);
            _backgroundBindings = LoadJson<Dictionary<string, BackgroundBinding>>(_backgroundBindingPath)
                ?? new Dictionary<string, BackgroundBinding>(StringComparer.OrdinalIgnoreCase);
            _customTitles = LoadJson<Dictionary<string, CustomTitleBinding>>(_customTitlePath)
                ?? new Dictionary<string, CustomTitleBinding>(StringComparer.OrdinalIgnoreCase);

            var settings = LoadJson<BackgroundSettings>(_backgroundSettingsPath);
            if (settings != null && settings.Opacity >= 0 && settings.Opacity <= 1)
            {
                _backgroundOpacity = settings.Opacity;
            }
            else
            {
                _backgroundOpacity = DefaultBackgroundOpacity;
            }

            _chipIconSize = settings?.IconSize is >= 16 and <= 40
                ? settings.IconSize
                : DefaultChipIconSize;

            _playerIdFontSize = settings?.PlayerIdFontSize is >= MinPlayerIdFontSize and <= MaxPlayerIdFontSize
                ? settings.PlayerIdFontSize
                : DefaultPlayerIdFontSize;

            _backgroundSolidColor = NormalizeColorHex(settings?.SolidColorHex) ?? string.Empty;

            var bwMessageConfig = LoadJson<BwMessageConfig>(_bwMessageConfigPath);
            _bwImageCaption = string.IsNullOrWhiteSpace(bwMessageConfig?.ImageCaption)
                ? DefaultBwImageCaption
                : bwMessageConfig!.ImageCaption.Trim();

            var queryable = LoadJson<List<string>>(_queryableIdsPath) ?? new List<string>();
            _queryableIds = new HashSet<string>(
                queryable.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var usage = LoadJson<NapcatUsageStats>(_napcatUsagePath) ?? new NapcatUsageStats();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var hasValidUsageDate = DateTime.TryParseExact(
                usage.Date ?? string.Empty,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedUsageDate);
            var usageDate = hasValidUsageDate
                ? parsedUsageDate.ToString("yyyy-MM-dd")
                : string.Empty;

            _usageDate = today;
            _usageCount = string.Equals(usageDate, today, StringComparison.Ordinal)
                ? Math.Max(0, usage.Count)
                : 0;
            _lastReportedDate = usage.LastReportedDate ?? string.Empty;

            if (!string.Equals(usage.Date, _usageDate, StringComparison.Ordinal)
                || usage.Count != _usageCount)
            {
                PersistNapcatUsageUnsafe();
            }
        }
    }

    public void UpsertQqBinding(string qq, string bjdName, string bjdUuid)
    {
        lock (_lock)
        {
            _qqBindings[qq] = new QqBinding
            {
                Qq = qq,
                BjdName = bjdName,
                BjdUuid = bjdUuid,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            SaveJson(_qqBindingPath, _qqBindings);

            // Include newly bound users in nightly scan targets by default.
            var normalizedName = bjdName?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedName) && _queryableIds.Add(normalizedName))
            {
                SaveJson(_queryableIdsPath, _queryableIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
            }
        }
    }

    public bool TryGetQqBinding(string qq, out QqBinding binding)
    {
        lock (_lock)
        {
            return _qqBindings.TryGetValue(qq, out binding!);
        }
    }

    public bool TryGetQqBindingByPlayerName(string playerName, out QqBinding binding)
    {
        binding = new QqBinding();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        var normalizedInput = playerName.Trim();
        lock (_lock)
        {
            foreach (var item in _qqBindings.Values)
            {
                if (string.Equals(item.BjdName?.Trim(), normalizedInput, StringComparison.OrdinalIgnoreCase))
                {
                    binding = item;
                    return true;
                }
            }
        }

        return false;
    }

    public void UpsertSkinBinding(string bjdUuid, string minecraftId, string avatarFileName)
    {
        lock (_lock)
        {
            _skinBindings[bjdUuid] = new SkinBinding
            {
                BjdUuid = bjdUuid,
                MinecraftId = minecraftId,
                AvatarFileName = avatarFileName,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            SaveJson(_skinBindingPath, _skinBindings);
        }
    }

    public bool TryGetSkinBinding(string bjdUuid, out SkinBinding binding)
    {
        lock (_lock)
        {
            return _skinBindings.TryGetValue(bjdUuid, out binding!);
        }
    }

    public void UpsertBackgroundBinding(string bjdUuid, string fileName)
    {
        lock (_lock)
        {
            _backgroundBindings[bjdUuid] = new BackgroundBinding
            {
                BjdUuid = bjdUuid,
                FileName = fileName,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            SaveJson(_backgroundBindingPath, _backgroundBindings);
        }
    }

    public bool TryGetBackgroundBinding(string bjdUuid, out BackgroundBinding binding)
    {
        lock (_lock)
        {
            return _backgroundBindings.TryGetValue(bjdUuid, out binding!);
        }
    }

    public double GetBackgroundOpacity()
    {
        lock (_lock)
        {
            return _backgroundOpacity;
        }
    }

    public void SetBackgroundOpacity(double opacity)
    {
        if (opacity < 0) opacity = 0;
        if (opacity > 1) opacity = 1;

        lock (_lock)
        {
            _backgroundOpacity = opacity;
            SaveJson(_backgroundSettingsPath, new BackgroundSettings
            {
                Opacity = _backgroundOpacity,
                IconSize = _chipIconSize,
                PlayerIdFontSize = _playerIdFontSize,
                SolidColorHex = _backgroundSolidColor
            });
        }
    }

    public int GetChipIconSize()
    {
        lock (_lock)
        {
            return _chipIconSize;
        }
    }

    public void SetChipIconSize(int size)
    {
        size = Math.Clamp(size, 16, 40);

        lock (_lock)
        {
            _chipIconSize = size;
            SaveJson(_backgroundSettingsPath, new BackgroundSettings
            {
                Opacity = _backgroundOpacity,
                IconSize = _chipIconSize,
                PlayerIdFontSize = _playerIdFontSize,
                SolidColorHex = _backgroundSolidColor
            });
        }
    }

    public string? GetBackgroundSolidColorHex()
    {
        lock (_lock)
        {
            return string.IsNullOrWhiteSpace(_backgroundSolidColor) ? null : _backgroundSolidColor;
        }
    }

    public void SetBackgroundSolidColorHex(string? colorHexWithoutHash)
    {
        lock (_lock)
        {
            _backgroundSolidColor = NormalizeColorHex(colorHexWithoutHash) ?? string.Empty;
            SaveJson(_backgroundSettingsPath, new BackgroundSettings
            {
                Opacity = _backgroundOpacity,
                IconSize = _chipIconSize,
                PlayerIdFontSize = _playerIdFontSize,
                SolidColorHex = _backgroundSolidColor
            });
        }
    }

    public int GetPlayerIdFontSize()
    {
        lock (_lock)
        {
            return _playerIdFontSize;
        }
    }

    public void SetPlayerIdFontSize(int size)
    {
        size = Math.Clamp(size, MinPlayerIdFontSize, MaxPlayerIdFontSize);

        lock (_lock)
        {
            _playerIdFontSize = size;
            SaveJson(_backgroundSettingsPath, new BackgroundSettings
            {
                Opacity = _backgroundOpacity,
                IconSize = _chipIconSize,
                PlayerIdFontSize = _playerIdFontSize,
                SolidColorHex = _backgroundSolidColor
            });
        }
    }

    public string GetBwImageCaption()
    {
        lock (_lock)
        {
            return _bwImageCaption;
        }
    }

    public void SetBwImageCaption(string? caption)
    {
        lock (_lock)
        {
            _bwImageCaption = (caption ?? string.Empty).Trim();
            SaveJson(_bwMessageConfigPath, new BwMessageConfig
            {
                ImageCaption = _bwImageCaption
            });
        }
    }

    public void UpsertCustomTitle(string bjdUuid, string title, string colorHex)
    {
        if (string.IsNullOrWhiteSpace(bjdUuid) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var normalizedColor = NormalizeColorHex(colorHex) ?? "FFFFFF";
        lock (_lock)
        {
            _customTitles[bjdUuid.Trim()] = new CustomTitleBinding
            {
                BjdUuid = bjdUuid.Trim(),
                Title = title.Trim(),
                ColorHex = normalizedColor,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            SaveJson(_customTitlePath, _customTitles);
        }
    }

    public bool TryGetCustomTitleByBjdUuid(string? bjdUuid, out CustomTitleBinding binding)
    {
        binding = new CustomTitleBinding();
        if (string.IsNullOrWhiteSpace(bjdUuid))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_customTitles.TryGetValue(bjdUuid.Trim(), out var value))
            {
                return false;
            }

            binding = value;
            return true;
        }
    }

    public void RecordQueryablePlayerId(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        lock (_lock)
        {
            if (_queryableIds.Add(playerId.Trim()))
            {
                SaveJson(_queryableIdsPath, _queryableIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
            }
        }
    }

    public List<string> GetQueryablePlayerIds()
    {
        lock (_lock)
        {
            return _queryableIds
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public List<string> GetBoundPlayerIds()
    {
        lock (_lock)
        {
            return _qqBindings.Values
                .Select(x => x.BjdName?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public int IncrementNapcatUsage()
    {
        lock (_lock)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!string.Equals(_usageDate, today, StringComparison.Ordinal))
            {
                _usageDate = today;
                _usageCount = 0;
            }

            _usageCount++;
            PersistNapcatUsageUnsafe();
            return _usageCount;
        }
    }

    public bool TryBuildDailyNapcatReport(DateTime now, out string message)
    {
        lock (_lock)
        {
            message = string.Empty;
            var today = now.ToString("yyyy-MM-dd");
            if (!string.Equals(_usageDate, today, StringComparison.Ordinal))
            {
                _usageDate = today;
                _usageCount = 0;
            }

            if (now.Hour < 23 || (now.Hour == 23 && now.Minute < 59))
            {
                return false;
            }

            if (string.Equals(_lastReportedDate, today, StringComparison.Ordinal))
            {
                return false;
            }

            message = $"NapCat 浠婃棩璋冪敤閲忕粺璁★紙{today}锛夛細{_usageCount}";
            _lastReportedDate = today;
            PersistNapcatUsageUnsafe();
            return true;
        }
    }

    private static void EnsureJsonFile(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "{}\n");
        }
    }

    private static void EnsureBackgroundSettingsFile(string path)
    {
        if (!File.Exists(path))
        {
            var text = JsonConvert.SerializeObject(new BackgroundSettings
            {
                Opacity = DefaultBackgroundOpacity,
                IconSize = DefaultChipIconSize,
                PlayerIdFontSize = DefaultPlayerIdFontSize,
                SolidColorHex = string.Empty
            }, Formatting.Indented);
            File.WriteAllText(path, text + Environment.NewLine);
        }
    }

    private static void EnsureBwMessageConfigFile(string path)
    {
        if (!File.Exists(path))
        {
            var text = JsonConvert.SerializeObject(new BwMessageConfig
            {
                ImageCaption = DefaultBwImageCaption
            }, Formatting.Indented);
            File.WriteAllText(path, text + Environment.NewLine);
        }
    }

    private static void EnsureQueryableIdsFile(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "[]\n");
        }
    }

    private static void EnsureNapcatUsageFile(string path)
    {
        if (!File.Exists(path))
        {
            var text = JsonConvert.SerializeObject(new NapcatUsageStats(), Formatting.Indented);
            File.WriteAllText(path, text + Environment.NewLine);
        }
    }

    private void PersistNapcatUsageUnsafe()
    {
        SaveJson(_napcatUsagePath, new NapcatUsageStats
        {
            Date = _usageDate,
            Count = _usageCount,
            LastReportedDate = _lastReportedDate
        });
    }

    private static string? NormalizeColorHex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        if (value.Length is not (3 or 6))
        {
            return null;
        }

        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return null;
            }
        }

        return value.ToUpperInvariant();
    }

    private static T? LoadJson<T>(string path)
    {
        var text = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<T>(text);
    }

    private static void SaveJson(string path, object data)
    {
        var text = JsonConvert.SerializeObject(data, Formatting.Indented);
        File.WriteAllText(path, text + Environment.NewLine);
    }
}

public class QqBinding
{
    public string Qq { get; set; } = string.Empty;
    public string BjdName { get; set; } = string.Empty;
    public string BjdUuid { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public class SkinBinding
{
    public string BjdUuid { get; set; } = string.Empty;
    public string MinecraftId { get; set; } = string.Empty;
    public string AvatarFileName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public class BackgroundBinding
{
    public string BjdUuid { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public class BackgroundSettings
{
    public double Opacity { get; set; } = 0.6;
    public int IconSize { get; set; } = 28;
    public int PlayerIdFontSize { get; set; } = 14;
    public string SolidColorHex { get; set; } = string.Empty;
}

public class BwMessageConfig
{
    public string ImageCaption { get; set; } = "想要显示自己的皮肤头像？请输入 !skin add 正版ID 即可使用自己的皮肤头像。";
}

public class CustomTitleBinding
{
    public string BjdUuid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "FFFFFF";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public class NapcatUsageStats
{
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public int Count { get; set; }
    public string LastReportedDate { get; set; } = string.Empty;
}
