using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs;

/// <summary>
/// 全局配置查看命令
/// </summary>
public static class ConfigCommand
{
    public static int Execute(string[] args)
    {
        if (args.Length == 0 || args[0].ToLowerInvariant() == "show")
        {
            var configStore = new ConfigStore();
            var config = configStore.LoadConfig();

            Console.WriteLine("当前全局配置:");
            Console.WriteLine(JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            Console.WriteLine($"配置文件路径: {Path.Combine(ConfigStore.ConfigDirectory, "config.json")}");
            return 0;
        }

        Console.Error.WriteLine("用法: remote-fs config show");
        return 1;
    }
}
