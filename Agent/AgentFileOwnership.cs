using System.Diagnostics;
using RemoteFs.Common;

namespace RemoteFs.Agent;

/// <summary>
/// 覆盖写会换成新文件。这里把原来的权限和属主记下来，写完再补回去。
/// </summary>
internal static class AgentFileOwnership
{
    public readonly record struct Snapshot(UnixFileMode? Mode, string? Owner);

    public static Snapshot Capture(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return default;
        }

        if (OperatingSystem.IsWindows())
        {
            return default;
        }

        UnixFileMode? mode = null;
        try
        {
            mode = File.GetUnixFileMode(path);
        }
        catch (Exception)
        {
            // 取不到权限不阻断写入
        }

        return new Snapshot(mode, QueryOwner(path));
    }

    public static void Restore(string path, Snapshot snapshot)
    {
        if (snapshot.Mode is { } mode && !OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, mode);
            }
            catch (Exception exception)
            {
                AuditLogger.Warn($"备用通道恢复权限失败 {path}: {exception.Message}");
            }
        }

        if (!string.IsNullOrEmpty(snapshot.Owner))
        {
            TryChown(path, snapshot.Owner);
        }
    }

    public static void ApplyMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private static string? QueryOwner(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "stat",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("%u:%g");
            start.ArgumentList.Add(path);

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

            var output = outputTask.GetAwaiter().GetResult().Trim();
            _ = errorTask.GetAwaiter().GetResult();
            return process.ExitCode != 0 || output.Length == 0 ? null : output;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryChown(string path, string owner)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "chown",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(owner);
            start.ArgumentList.Add(path);

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
                var error = errorTask.GetAwaiter().GetResult().Trim();
                AuditLogger.Warn($"备用通道恢复属主失败 {path}: {error}");
            }
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"备用通道恢复属主失败 {path}: {exception.Message}");
        }
    }
}
