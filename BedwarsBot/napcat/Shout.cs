using Newtonsoft.Json;

namespace BedwarsBot;

public sealed class Shout
{
    private readonly string _configPath;
    private readonly object _lock = new();
    private int _isBroadcasting;

    public Shout(string rootDir)
    {
        var pzDir = Path.Combine(rootDir, "pz");
        Directory.CreateDirectory(pzDir);
        _configPath = Path.Combine(pzDir, "shoutconfig.json");
        EnsureConfigFile();
    }

    public string GetText()
    {
        lock (_lock)
        {
            var cfg = LoadConfigInternal();
            return cfg.Text ?? string.Empty;
        }
    }

    public string UpdateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "❌ 群发内容不能为空。";
        }

        lock (_lock)
        {
            var cfg = LoadConfigInternal();
            cfg.Text = text.Trim();
            SaveConfigInternal(cfg);
        }

        return "✅ 群发内容已更新。";
    }

    public async Task<string> StartBroadcastAsync(NapcatBot bot)
    {
        if (Interlocked.CompareExchange(ref _isBroadcasting, 1, 0) != 0)
        {
            return "⏳ 群发任务正在执行中，请稍后再试。";
        }

        var text = GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            Interlocked.Exchange(ref _isBroadcasting, 0);
            return "❌ 群发内容为空，请先使用 /群发编辑 文本";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var groups = await bot.GetGroupIdsAsync();
                if (groups.Count == 0)
                {
                    Console.WriteLine("[NapCat群发] 未获取到群列表。");
                    return;
                }

                var excluded = new HashSet<string>(
                    LoadConfigInternal().ExcludedGroupIds ?? new List<string>(),
                    StringComparer.Ordinal);
                var targets = groups.Where(g => !excluded.Contains(g)).ToList();
                if (targets.Count == 0)
                {
                    Console.WriteLine("[NapCat群发] 所有群都在白名单(不发送列表)中，已跳过。");
                    return;
                }

                for (var i = 0; i < targets.Count; i++)
                {
                    var groupId = targets[i];
                    await bot.SendTextAsync(groupId, text);
                    Console.WriteLine($"[NapCat群发] 已发送 group={groupId}");

                    if (i < targets.Count - 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NapCat群发] 执行失败: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isBroadcasting, 0);
            }
        });

        return "✅ 群发任务已启动（每个群间隔 15 秒）。";
    }

    private void EnsureConfigFile()
    {
        if (File.Exists(_configPath))
        {
            return;
        }

        var cfg = new ShoutConfig
        {
            Text = "请使用 /群发编辑 文本 设置群发内容",
            ExcludedGroupIds = new List<string>()
        };
        var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
        File.WriteAllText(_configPath, json);
    }

    private ShoutConfig LoadConfigInternal()
    {
        try
        {
            var text = File.ReadAllText(_configPath);
            return JsonConvert.DeserializeObject<ShoutConfig>(text) ?? new ShoutConfig();
        }
        catch
        {
            return new ShoutConfig();
        }
    }

    private void SaveConfigInternal(ShoutConfig config)
    {
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        File.WriteAllText(_configPath, json);
    }
}

public sealed class ShoutConfig
{
    public string Text { get; set; } = string.Empty;
    // 群白名单：这些群不会收到 /群发 内容
    public List<string> ExcludedGroupIds { get; set; } = new();
}
