using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;
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
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            switch (command)
            {
                case "serve":
                    return await ServeAsync();

                case "hosts":
                    return HostsCommand.Execute(rest);

                case "config":
                    return ConfigCommand.Execute(rest);

                case "version":
                case "--version":
                case "-v":
                    Console.WriteLine("remote-fs 1.0.0");
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

        // 日志文件路径，失败时降级为仅 stderr 输出
        var logDirectory = ConfigStore.ConfigDirectory;
        try
        {
            Directory.CreateDirectory(logDirectory);
            AuditLogger.LogFilePath = Path.Combine(logDirectory, "remote-fs.log");
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"无法创建日志目录，仅输出到 stderr: {exception.Message}");
        }

        var service = new RemoteFsService(config, configStore);

        var tools = new List<ToolDefinition>();
        tools.AddRange(new FileReadTools(service).Build());
        tools.AddRange(new FileWriteTools(service).Build());
        tools.AddRange(new CommandTools(service).Build());
        tools.AddRange(new TransferTools(service).Build());

        AuditLogger.Info($"已注册 {tools.Count} 个 MCP 工具");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var server = new McpStdioServer(tools);
        await server.RunAsync(cancellation.Token);
        return 0;
    }

    /// <summary>
    /// 打印用法说明
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("""
            remote-fs - 远程服务器文件管理 MCP 服务

            用法:
              remote-fs serve                    启动 MCP stdio 服务（供 Claude Code 等客户端连接）
              remote-fs hosts list               列出已配置主机
              remote-fs hosts add <别名>         交互式添加主机配置
              remote-fs hosts remove <别名>      删除主机配置
              remote-fs hosts fingerprint <别名>  查看主机密钥指纹
              remote-fs config show              查看当前全局配置
              remote-fs version                  显示版本号

            配置文件:
              ~/.remote-fs/hosts.json   主机列表
              ~/.remote-fs/config.json  全局配置
            """);
    }
}
