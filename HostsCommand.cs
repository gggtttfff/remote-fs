using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Ssh;

namespace RemoteFs;

/// <summary>
/// 主机管理命令：list / add / remove / fingerprint
/// </summary>
public static class HostsCommand
{
    public static int Execute(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法: remote-fs hosts <list|add|remove|fingerprint> [参数]");
            return 1;
        }

        var action = args[0].ToLowerInvariant();
        var configStore = new ConfigStore();

        return action switch
        {
            "list" => ListHosts(configStore),
            "add" => AddHost(configStore, args.Skip(1).ToArray()),
            "remove" => RemoveHost(configStore, args.Skip(1).ToArray()),
            "fingerprint" => ShowFingerprint(configStore, args.Skip(1).ToArray()),
            _ => UnknownAction(action)
        };
    }

    /// <summary>
    /// 列出主机
    /// </summary>
    private static int ListHosts(ConfigStore configStore)
    {
        var hosts = configStore.LoadHosts();
        if (hosts.Count == 0)
        {
            Console.WriteLine("尚未配置任何主机。使用 remote-fs hosts add <别名> 添加。");
            return 0;
        }

        var config = configStore.LoadConfig();
        foreach (var host in hosts)
        {
            var marker = host.Name == config.DefaultHost ? " (默认)" : string.Empty;
            var scope = host.AllowedRoots.Count > 0
                ? string.Join(", ", host.AllowedRoots)
                : "未限制";
            Console.WriteLine($"{host.Name}{marker}: {host.Username}@{host.Host}:{host.Port} "
                + $"认证={host.Auth} 允许范围={scope}");
        }
        return 0;
    }

    /// <summary>
    /// 交互式添加主机
    /// </summary>
    private static int AddHost(ConfigStore configStore, string[] args)
    {
        var hosts = configStore.LoadHosts();

        var profile = new HostProfile
        {
            Name = args.Length > 0 ? args[0] : Prompt("主机别名", "web1"),
            Host = Prompt("主机地址", "192.168.1.10"),
            Username = Prompt("登录用户名", "root"),
            Port = int.TryParse(Prompt("端口", "22"), out var port) ? port : 22
        };

        var authInput = Prompt("认证方式 (key/password/auto)", "auto").ToLowerInvariant();
        profile.Auth = authInput switch
        {
            "key" => AuthMethod.Key,
            "password" => AuthMethod.Password,
            _ => AuthMethod.Auto
        };

        if (profile.Auth is AuthMethod.Key or AuthMethod.Auto)
        {
            var keyPath = Prompt("私钥路径", "~/.ssh/id_rsa");
            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                profile.PrivateKeyPath = keyPath;
            }
        }

        if (profile.Auth is AuthMethod.Password or AuthMethod.Auto)
        {
            var passwordVariable = Prompt("密码读取的环境变量名（留空则交互输入）", "");
            if (!string.IsNullOrWhiteSpace(passwordVariable))
            {
                profile.PasswordEnvironmentVariable = passwordVariable;
            }
            else
            {
                var password = ReadPassword("登录密码（留空表示不使用密码认证）");
                if (!string.IsNullOrEmpty(password))
                {
                    profile.Password = password;
                }
            }
        }

        var passphrase = ReadPassword("私钥密码（没有则直接回车）");
        if (!string.IsNullOrEmpty(passphrase))
        {
            profile.PrivateKeyPassphrase = passphrase;
        }

        var allowedRoots = Prompt("允许访问的路径根目录（多个用逗号分隔，留空表示不限制）", "");
        if (!string.IsNullOrWhiteSpace(allowedRoots))
        {
            profile.AllowedRoots = allowedRoots
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(root => root.Trim())
                .Where(root => root.Length > 0)
                .ToList();
        }

        var strictInput = Prompt("是否启用主机密钥指纹校验 (y/n)", "y").ToLowerInvariant();
        profile.StrictHostKeyChecking = strictInput is "y" or "yes";

        // 名称唯一性校验
        if (hosts.Any(host => string.Equals(host.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"主机别名 '{profile.Name}' 已存在，请换一个名称或先 remove。");
            return 1;
        }

        hosts.Add(profile);
        configStore.SaveHosts(hosts);

        Console.WriteLine();
        Console.WriteLine($"已添加主机 {profile.Name}。");
        Console.WriteLine($"接下来执行 remote-fs hosts fingerprint {profile.Name} 获取指纹，"
            + "并填入 hosts.json 的 knownHostKeyFingerprint 字段。");

        return 0;
    }

    /// <summary>
    /// 删除主机
    /// </summary>
    private static int RemoveHost(ConfigStore configStore, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: remote-fs hosts remove <别名>");
            return 1;
        }

        var name = args[0];
        var hosts = configStore.LoadHosts();
        var target = hosts.FirstOrDefault(host =>
            string.Equals(host.Name, name, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            Console.Error.WriteLine($"未找到主机 '{name}'");
            return 1;
        }

        hosts.Remove(target);
        configStore.SaveHosts(hosts);
        Console.WriteLine($"已删除主机 {name}");
        return 0;
    }

    /// <summary>
    /// 显示主机密钥指纹
    /// </summary>
    private static int ShowFingerprint(ConfigStore configStore, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: remote-fs hosts fingerprint <别名>");
            return 1;
        }

        var name = args[0];
        HostProfile profile;
        try
        {
            profile = configStore.RequireHost(name);
        }
        catch (KeyNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        try
        {
            var factory = new SshConnectionFactory(configStore.LoadConfig());
            using var client = factory.CreateSshClient(profile, out var fingerprint);
            Console.WriteLine($"主机 {name} 的密钥指纹: {fingerprint}");
            Console.WriteLine("请将其填入 hosts.json 对应主机的 knownHostKeyFingerprint 字段。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"获取指纹失败: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 未知子命令提示
    /// </summary>
    private static int UnknownAction(string action)
    {
        Console.Error.WriteLine($"未知操作: {action}");
        Console.WriteLine("可用操作: list, add, remove, fingerprint");
        return 1;
    }

    /// <summary>
    /// 读取一行输入，带默认值
    /// </summary>
    private static string Prompt(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    /// <summary>
    /// 读取密码，输入不回显
    /// </summary>
    private static string ReadPassword(string label)
    {
        Console.Write($"{label}: ");
        try
        {
            var password = new string(ReadHiddenInput());
            Console.WriteLine();
            return password;
        }
        catch (PlatformNotSupportedException)
        {
            // 非交互终端回退到普通读取
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// 逐字符读取隐藏输入
    /// </summary>
    private static char[] ReadHiddenInput()
    {
        var buffer = new char[0];
        var input = Console.ReadKey(intercept: true);
        while (input.Key != ConsoleKey.Enter)
        {
            if (input.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer = buffer[..^1];
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(input.KeyChar))
            {
                buffer = [.. buffer, input.KeyChar];
                Console.Write('*');
            }
            input = Console.ReadKey(intercept: true);
        }
        return buffer;
    }
}
