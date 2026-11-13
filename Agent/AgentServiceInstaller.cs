using System.Diagnostics;
using RemoteFs.Common;

namespace RemoteFs.Agent;

/// <summary>
/// 把备用端口注册成 systemd 服务。装好之后开机自启，进程退出会拉起，替换程序文件也会重启。
/// </summary>
public static class AgentServiceInstaller
{
    public const string ServiceName = "remote-fs-agent";

    public static int Install(int port)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("注册系统服务只在 Linux 上可用。");
            return 1;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Console.Error.WriteLine("无法确定当前程序路径，没有注册服务。");
            return 1;
        }

        var unitDirectory = "/etc/systemd/system";
        try
        {
            Directory.CreateDirectory(unitDirectory);
            File.WriteAllText(Path.Combine(unitDirectory, ServiceName + ".service"), BuildServiceUnit(executablePath, port));
            File.WriteAllText(Path.Combine(unitDirectory, ServiceName + ".path"), BuildPathUnit(executablePath));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"写入 systemd 单元失败: {exception.Message}");
            return 1;
        }

        if (!Run("daemon-reload")
            || !Run("enable", "--now", ServiceName + ".path")
            || !Run("enable", "--now", ServiceName + ".service"))
        {
            return 1;
        }

        Console.WriteLine($"已注册并启动 {ServiceName}，端口 {port}。");
        Console.WriteLine("开机和进程退出都会自动拉起。替换程序文件后服务会自己重启，不用再手工执行 listen。");
        AuditLogger.Info($"已注册 systemd 服务 {ServiceName}，端口 {port}");
        return 0;
    }

    public static bool HasInstallFlag(string[] args)
    {
        return args.Any(argument => argument is "--install-service" or "--service");
    }

    private static string BuildServiceUnit(string executablePath, int port)
    {
        var workDirectory = Path.GetDirectoryName(executablePath) ?? "/";
        return $"""
            [Unit]
            Description=remote-fs agent
            After=network-online.target
            Wants=network-online.target
            StartLimitIntervalSec=60
            StartLimitBurst=5

            [Service]
            Type=simple
            WorkingDirectory={workDirectory}
            ExecStart="{executablePath}" listen --port {port}
            Restart=always
            RestartSec=2

            [Install]
            WantedBy=multi-user.target
            """;
    }

    /// <summary>程序文件被替换时重启服务，避免每次更新都手工再跑一遍。</summary>
    private static string BuildPathUnit(string executablePath)
    {
        return $"""
            [Unit]
            Description=Restart remote-fs agent when the binary changes

            [Path]
            PathChanged={executablePath}
            Unit={ServiceName}.service

            [Install]
            WantedBy=multi-user.target
            """;
    }

    private static bool Run(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "systemctl",
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                Console.Error.WriteLine("无法启动 systemctl");
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }

            Console.Error.WriteLine($"systemctl {string.Join(' ', arguments)} 失败，退出码 {process.ExitCode}");
            return false;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"执行 systemctl 失败: {exception.Message}");
            return false;
        }
    }
}
