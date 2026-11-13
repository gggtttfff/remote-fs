using System.Diagnostics;
using RemoteFs.Common;

namespace RemoteFs.Agent;

/// <summary>
/// 覆盖写会换成新文件。这里把原来的权限和属主记下来，写完再补回去。
/// </summary>
internal static class AgentFileOwnership
{
    public readonly record struct Snapshot(string? OctalMode, string? Owner);

    public static Snapshot Capture(string path)
    {
        if (OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return default;
        }

        return new Snapshot(Query("stat", "-c", "%a", "--", path), Query("stat", "-c", "%u:%g", "--", path));
    }

    public static void Restore(string path, Snapshot snapshot)
    {
        if (!string.IsNullOrEmpty(snapshot.Owner))
        {
            Run("chown", snapshot.Owner, "--", path);
        }

        if (!string.IsNullOrEmpty(snapshot.OctalMode))
        {
            Run("chmod", snapshot.OctalMode, "--", path);
        }
    }

    public static void ApplyMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Run("chmod", Convert.ToString(mode, 8), "--", path);
    }

    private static string? Query(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            _ = errorTask.GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Run(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2_000))
            {
                process.Kill(entireProcessTree: true);
                return;
            }

            _ = outputTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                AuditLogger.Warn($"备用通道 {fileName} 失败 {arguments[^1]}: {errorTask.GetAwaiter().GetResult().Trim()}");
            }
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"备用通道 {fileName} 失败: {exception.Message}");
        }
    }
}
