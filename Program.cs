using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Logging;
using RemoteFs.Mcp;
using RemoteFs.Services;
using RemoteFs.Tools;

namespace RemoteFs;

/// <summary>
/// 应用入口：支持 serve（启动 MCP 服务）与 hosts（主机管理）等子命令
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 控制台文本输出统一用 UTF-8，避免 Windows 默认代码页导致中文乱码
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (Exception)
        {
            // 输出被重定向等场景下设置可能失败，不影响主流程
        }

        try
        {
            if (args.Length == 0)
            {
                // 双击 exe 时直接打开配置界面；没有图形界面的平台仍打印用法
                return OpenGuiOrPrintUsage();
            }

            var command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (command)
            {
                case "serve":
                    return await ServeAsync();

                case "hosts":
                    return HostsCommand.Execute(rest);

                case "gui":
                    return RunGui();

                case "config":
                    return ConfigCommand.Execute(rest);

                case "version":
                case "--version":
                case "-v":
                    Console.WriteLine($"remote-fs {McpProtocol.ServerVersion}");
                    return 0;

                case "help":
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"未知命令: {command}");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 启动 MCP stdio 服务
    /// </summary>
    private static async Task<int> ServeAsync()
    {
        var configStore = new ConfigStore();
        var config = configStore.LoadConfig();

        // 日志开关与落盘位置都取决于全局配置
        AuditLogger.Enabled = config.AuditLogEnabled;
        ConfigureAuditLog(config);

        // 日志就绪后再准备配置目录，这样旧配置迁移的记录能落进日志文件
        ConfigStore.EnsureInitialized();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            using (var service = new RemoteFsService(config, configStore))
            {
                var tools = new List<ToolDefinition>();
                tools.AddRange(new FileReadTools(service).Build());
                tools.AddRange(new FileWriteTools(service).Build());
                tools.AddRange(new CommandTools(service).Build());
                tools.AddRange(new JobTools(service).Build());
                tools.AddRange(new TransferTools(service).Build());

                AuditLogger.Info($"已注册 {tools.Count} 个 MCP 工具");

                // 主机解析器只用于日志归类，日志记不住时也不该影响正常调用
                var server = new McpStdioServer(
                    tools,
                    () =>
                    {
                        try
                        {
                            return service.ResolveHost(null).Name;
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    },
                    (toolName, hostName) =>
                    {
                        if (hostName is not null)
                        {
                            service.Cache.InvalidateHost(hostName);
                        }
                    });

                // 兜底：进程被强制结束时也尽量把远程任务收掉，避免留下孤儿构建
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try
                    {
                        service.Jobs.KillAllStarted();
                    }
                    catch (Exception)
                    {
                        // 进程正在退出，这里没有补救余地
                    }
                };

                await server.RunAsync(cancellation.Token);
            }
        }
        finally
        {
            // service 释放时会先终止远程任务再关闭连接池，之后才轮到日志落盘
            AuditLogger.Flush();
        }

        return 0;
    }

    /// <summary>
    /// 无参数启动：Windows 版打开图形配置界面，其他平台没有界面则打印用法
    /// </summary>
    private static int OpenGuiOrPrintUsage()
    {
#if WINDOWS_GUI
        return RunGui();
#else
        PrintUsage();
        return 1;
#endif
    }

    /// <summary>
    /// 启动图形配置界面，仅 Windows 版本可用
    /// </summary>
    private static int RunGui()
    {
#if WINDOWS_GUI
        // 界面进程从资源管理器启动时看不到控制台，日志必须落到文件才能排查
        var configStore = new ConfigStore();
        var config = configStore.LoadConfig();
        AuditLogger.Enabled = config.AuditLogEnabled;
        ConfigureAuditLog(config);
        ConfigStore.EnsureInitialized();
        // 仅 Windows 目标会编译 Gui 目录，此处用全限定名避免引入不存在的 using
        var exitCode = RemoteFs.Gui.GuiLauncher.Run();
        AuditLogger.Flush();
        return exitCode;
#else
        Console.Error.WriteLine("图形界面仅在 Windows 版本中提供，当前平台请使用 hosts 子命令。");
        return 1;
#endif
    }

    /// <summary>
    /// 初始化日志：写到 exe 同级的 log 目录，目录不可写时降级为仅 stderr
    /// </summary>
    private static void ConfigureAuditLog(AppConfig config)
    {
        AuditLogger.Initialize(LogSettings.FromConfig(config));
    }

    /// <summary>
    /// 打印用法说明
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("""
            remote-fs - 远程服务器文件管理 MCP 服务

            用法:
              remote-fs                          打开图形配置界面（仅 Windows，等同于 gui）
              remote-fs serve                    启动 MCP stdio 服务（供 Claude Code 等客户端连接）
              remote-fs gui                      打开主机配置图形界面（仅 Windows）
              remote-fs hosts list               列出已配置主机
              remote-fs hosts add <别名>         交互式添加主机配置
              remote-fs hosts remove <别名>      删除主机配置
              remote-fs hosts fingerprint <别名>  查看主机密钥指纹
              remote-fs hosts import [别名...]  从 ~/.ssh/config 导入主机
              remote-fs config show              查看当前全局配置
              remote-fs version                  显示版本号
            """);

        // 配置目录随安装位置走，这里必须如实打印，避免用户按旧路径去找
        Console.WriteLine($"配置文件目录: {ConfigStore.ConfigDirectory}");
        Console.WriteLine("  hosts.json   主机列表");
        Console.WriteLine("  config.json  全局配置");
    }
}
