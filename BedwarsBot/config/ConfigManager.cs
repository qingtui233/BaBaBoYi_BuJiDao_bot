using Newtonsoft.Json;

namespace BedwarsBot;

public static class ConfigManager
{
    private static readonly JsonSerializerSettings ConfigJsonSettings = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace
    };

    public static string GetConfigFilePath()
    {
        return Path.Combine(ResolveRootDirectory(), "pz", "config.json");
    }

    public static BotConfig LoadConfig()
    {
        // 在程序运行目录找 pz 文件夹
        var file = GetConfigFilePath();
        var folder = Path.GetDirectoryName(file) ?? Path.Combine(ResolveRootDirectory(), "pz");

        // 1. 没文件夹就创建
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        // 2. 没文件就生成默认的
        if (!File.Exists(file))
        {
            var config = new BotConfig();
            File.WriteAllText(file, JsonConvert.SerializeObject(config, Formatting.Indented));
            
            Console.WriteLine("==========================================");
            Console.WriteLine($"[提示] 配置文件已生成！");
            Console.WriteLine($"[位置] {file}");
            Console.WriteLine("请去填入 AppID 和 Secret，然后重启程序。");
            Console.WriteLine("==========================================");
            return null; // 返回 null 代表需要停止程序
        }

        // 3. 有文件就读取
        var content = File.ReadAllText(file);
        return JsonConvert.DeserializeObject<BotConfig>(content, ConfigJsonSettings);
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
}
