using System.Diagnostics;
using System.Text;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Mcp;
using RemoteFs.Services;

namespace RemoteFs.Agent;

internal sealed partial class AgentToolExecutor
{
    private string Move(ToolCallContext context)
    {
        var source = Prepare(context.RequireString("source"), mustExist: true);
        var destination = Prepare(context.RequireString("destination"), mustExist: false);
        var overwrite = context.GetBool("overwrite");
        if (Exists(destination) && !overwrite)
        {
            throw new InvalidOperationException($"目标路径 {destination} 已存在。如需覆盖请设置 overwrite=true。");
        }

        if (overwrite && File.Exists(destination) && !IsSymlink(destination))
        {
            File.Delete(destination);
        }

        if (Directory.Exists(source) && !IsSymlink(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite);
        }

        return $"已将 {source} 移动到 {destination}";
    }

    private string Copy(ToolCallContext context)
    {
        var source = Prepare(context.RequireString("source"), mustExist: true);
        var destination = Prepare(context.RequireString("destination"), mustExist: false);
        var recursive = context.GetBool("recursive", true);
        if (Directory.Exists(source) && !IsSymlink(source))
        {
            if (!recursive)
            {
                throw new InvalidOperationException($"源路径 {source} 是目录，需要 recursive=true 才能复制");
            }

            CopyDirectory(source, destination, 0);
        }
        else
        {
            CopyFile(source, destination);
        }

        return $"已将 {source} 复制到 {destination}";
    }

    private string Chmod(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var permissionsText = context.RequireString("permissions");
        if (!TryParseOctal(permissionsText, out var mode))
        {
            throw new ArgumentException($"权限值 '{permissionsText}' 不是合法的八进制权限，示例：644、755");
        }

        AgentFileOwnership.ApplyMode(path, mode);
        var octal = Convert.ToString(mode, 8).PadLeft(3, '0');
        return $"已将 {path} 权限修改为 {octal}";
    }

    private string Symlink(ToolCallContext context)
    {
        var target = Prepare(context.RequireString("target"), mustExist: false);
        var linkPath = Prepare(context.RequireString("linkPath"), mustExist: false);
        if (Exists(linkPath))
        {
            throw new InvalidOperationException($"链接路径 {linkPath} 已存在");
        }

        File.CreateSymbolicLink(linkPath, target);
        return $"已创建符号链接 {linkPath} -> {target}";
    }

    private string Search(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var pattern = context.RequireString("pattern");
        var filePattern = context.GetString("filePattern");
        var comparison = context.GetBool("caseSensitive")
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var maxResults = Math.Clamp(context.GetInt("maxResults") ?? 50, 1, _config.MaxSearchResults);
        var recursive = context.GetBool("recursive", true);
        var encodingName = ResolveEncoding(context);
        var builder = new StringBuilder();
        var matches = 0;
        var skipped = 0;
        foreach (var file in EnumerateFiles(path, recursive, filePattern))
        {
            if (matches >= maxResults)
            {
                break;
            }

            try
            {
                // 解码要宽松：非法字节用替换字符兜住，一个坏文件不该让整次搜索失败
                var decoding = TextEncodingResolver.ResolveLenient(encodingName) ?? TextEncodingResolver.LenientUtf8;
                using var reader = new StreamReader(file, decoding, detectEncodingFromByteOrderMarks: true);
                var number = 0;
                while (reader.ReadLine() is { } line)
                {
                    number++;
                    if (!line.Contains(pattern, comparison))
                    {
                        continue;
                    }

                    builder.AppendLine($"{file}:{number}: {line.Trim()}");
                    matches++;
                    if (matches >= maxResults)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                skipped++;
                AuditLogger.Warn($"备用通道搜索跳过文件 {file}: {exception.Message}");
            }
        }

        if (matches == 0)
        {
            return skipped > 0
                ? $"在 {path} 中没有找到 \"{pattern}\"（已跳过 {skipped} 个无法读取的文件）"
                : $"在 {path} 中没有找到 \"{pattern}\"";
        }

        if (matches >= maxResults)
        {
            builder.AppendLine($"[已达到结果上限 {maxResults}]");
        }

        if (skipped > 0)
        {
            builder.AppendLine($"[已跳过 {skipped} 个无法读取的文件]");
        }

        return Truncate(builder.ToString());
    }

    private string Tree(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var depth = Math.Clamp(context.GetInt("depth") ?? 2, 1, _config.MaxTreeDepth);
        var builder = new StringBuilder();
        builder.AppendLine(path);
        var count = 0;
        AppendTree(path, builder, 1, depth, ref count);
        if (count >= _config.MaxListEntries)
        {
            builder.AppendLine($"[条目数达到上限 {_config.MaxListEntries}，未继续展开]");
        }

        return Truncate(builder.ToString());
    }

    private (bool Ok, string Text) ExecuteCommand(ToolCallContext context)
    {
        var command = context.RequireString("command");
        var guardHost = _allowEverything ? new HostProfile { Name = "local" } : _localHost;
        _guard.EnsureCommandAllowed(guardHost, command);
        if (context.GetBool("background"))
        {
            return (true, StartBackground(context, command));
        }

        return RunForeground(context, command);
    }

    private (bool Ok, string Text) RunForeground(ToolCallContext context, string command)
    {
        var timeout = Math.Clamp(context.GetInt("timeout") ?? 60_000, 1_000, 3_600_000);
        var encodingName = ResolveEncoding(context);

        // 用 bash 而不是 sh：SSH 路径走的是登录用户的 shell，[[ ]] 这类 bash 语法在那边能用，
        // 备用通道换成 sh 就会出现两条路径行为不一致
        var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        var start = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"无法启动 {shell}");

        // 读原始字节再按指定编码解码。直接让 .NET 按默认 UTF-8 读，
        // GBK 输出会变成乱码。
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        var stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream);
        var finished = process.WaitForExit(timeout);
        if (!finished)
        {
            process.Kill(entireProcessTree: true);
        }

        var stdout = DecodeOutput(stdoutTask.GetAwaiter().GetResult(), encodingName);
        var stderr = DecodeOutput(stderrTask.GetAwaiter().GetResult(), encodingName);
        var builder = new StringBuilder();
        builder.AppendLine($"命令: {command}");
        builder.AppendLine(finished ? $"退出码: {process.ExitCode}" : "状态: 已超时终止");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine("--- 标准输出 ---");
            builder.AppendLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine("--- 标准错误 ---");
            builder.AppendLine(stderr.TrimEnd());
        }

        var text = Truncate(builder.ToString());
        return (finished && process.ExitCode == 0, text);
    }

    /// <summary>
    /// 按指定编码解码命令输出。解不出来时退回替换字符，不能让整条命令失败。
    /// </summary>
    private static string DecodeOutput(byte[] content, string encodingName)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var decoded = TextEncodingResolver.Decode(content, encodingName, truncated: false);
        if (decoded.Success)
        {
            return decoded.Text ?? string.Empty;
        }

        var lenient = TextEncodingResolver.ResolveLenient(encodingName) ?? TextEncodingResolver.LenientUtf8;
        return lenient.GetString(content);
    }

    /// <summary>
    /// 读出流的全部字节。Stream 本身没有 ReadToEndAsync，只能自己转存。
    /// </summary>
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private string StartBackground(ToolCallContext context, string command)
    {
        var ttlSeconds = Math.Clamp((context.GetInt("ttlMinutes") ?? _config.BackgroundJobTtlMinutes) * 60, 60, 86_400);
        var jobRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remote-fs", "jobs");
        var jobId = $"job-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var directory = Path.Combine(jobRoot, jobId);
        Directory.CreateDirectory(directory);
        var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        var useSetsid = File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid");
        var encodingName = ResolveEncoding(context);

        // cmd / run.sh / watchdog.sh 恒 UTF-8：远端 shell 按字节读这些文件，
        // 传成 GBK 会把命令本身写坏。只有 out / err 跟随 encoding。
        File.WriteAllText(Path.Combine(directory, "cmd"), command + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "run.sh"), RemoteJobScripts.BuildRunScript(directory, shell, useSetsid), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "watchdog.sh"), RemoteJobScripts.BuildWatchdogScript(directory, ttlSeconds), new UTF8Encoding(false));

        // 记录输出编码，作为 MCP 重启后接管遗留任务的依据
        File.WriteAllText(Path.Combine(directory, "encoding"), encodingName + "\n", new UTF8Encoding(false));

        var launch = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        launch.ArgumentList.Add("-c");
        launch.ArgumentList.Add(RemoteJobScripts.BuildLaunchCommand(directory, useSetsid));
        using var process = Process.Start(launch)
            ?? throw new InvalidOperationException("无法启动后台任务");
        process.WaitForExit(5_000);
        return $"任务已提交: {jobId}{Environment.NewLine}任务目录: {directory}{Environment.NewLine}后续: 用 remote_job_status 拉取输出，用 remote_job_kill 终止。";
    }

    private string JobStatus(ToolCallContext context)
    {
        var directory = JobDirectory(context.RequireString("jobId"));
        var stdoutOffset = context.GetLong("stdoutOffset") ?? 0;
        var stderrOffset = context.GetLong("stderrOffset") ?? 0;
        var encodingName = ResolveJobEncoding(directory, context.GetString("encoding"));
        var stdout = ReadFromOffset(Path.Combine(directory, "out"), stdoutOffset, encodingName);
        var stderr = ReadFromOffset(Path.Combine(directory, "err"), stderrOffset, encodingName);
        var exitPath = Path.Combine(directory, "exit");
        var state = File.Exists(exitPath) ? $"已结束（退出码 {File.ReadAllText(exitPath).Trim()}）" : "运行中";
        var builder = new StringBuilder();
        builder.AppendLine($"状态: {state}");
        builder.AppendLine("--- 标准输出 ---");
        builder.AppendLine(stdout.Text);
        builder.AppendLine($"nextStdoutOffset: {stdout.NextOffset}");
        builder.AppendLine("--- 标准错误 ---");
        builder.AppendLine(stderr.Text);
        builder.AppendLine($"nextStderrOffset: {stderr.NextOffset}");
        return Truncate(builder.ToString());
    }

    /// <summary>
    /// 任务输出的编码以任务目录里记录的为准，工具参数可以做单次覆盖。
    /// 记录文件是事实来源，MCP 重启后接管遗留任务仍知道该用什么解码。
    /// </summary>
    private static string ResolveJobEncoding(string directory, string? overrideName)
    {
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            return TextEncodingResolver.Normalize(overrideName);
        }

        var encodingPath = Path.Combine(directory, "encoding");
        if (File.Exists(encodingPath))
        {
            var recorded = File.ReadAllText(encodingPath).Trim();
            if (!string.IsNullOrEmpty(recorded))
            {
                return TextEncodingResolver.Normalize(recorded);
            }
        }

        return TextEncodingResolver.Normalize("utf-8");
    }

    private string JobList(ToolCallContext context)
    {
        var jobRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remote-fs", "jobs");
        if (!Directory.Exists(jobRoot))
        {
            return "没有任务";
        }

        var limit = Math.Clamp(context.GetInt("limit") ?? 20, 1, 200);
        var includeFinished = context.GetBool("includeFinished", true);
        var entries = new DirectoryInfo(jobRoot).EnumerateDirectories()
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .Where(item => includeFinished || !File.Exists(Path.Combine(item.FullName, "exit")))
            .Take(limit)
            .ToList();
        if (entries.Count == 0)
        {
            return "没有符合条件的任务";
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            var exitPath = Path.Combine(entry.FullName, "exit");
            var state = File.Exists(exitPath) ? $"已结束 {File.ReadAllText(exitPath).Trim()}" : "运行中";
            builder.AppendLine($"{entry.Name}  {state}");
        }

        return builder.ToString().TrimEnd();
    }

    private string JobKill(ToolCallContext context)
    {
        var directory = JobDirectory(context.RequireString("jobId"));
        var pidText = File.ReadAllText(Path.Combine(directory, "pid")).Trim();
        if (!int.TryParse(pidText, out var pid))
        {
            throw new InvalidOperationException("任务没有可用的 pid");
        }

        var force = context.GetBool("force");
        var launch = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        launch.ArgumentList.Add("-c");
        launch.ArgumentList.Add(RemoteJobScripts.BuildKillCommand(pid, force));
        using var process = Process.Start(launch)
            ?? throw new InvalidOperationException("无法终止任务");
        process.WaitForExit(10_000);
        return $"已终止任务 {Path.GetFileName(directory)}";
    }

    private void CopyFile(string source, string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var snapshot = AgentFileOwnership.Capture(source);
        File.Copy(source, destination, overwrite: true);
        AgentFileOwnership.Restore(destination, snapshot);
    }

    private void CopyDirectory(string source, string destination, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("目录层级过深");
        }

        Directory.CreateDirectory(destination);
        AgentFileOwnership.Restore(destination, AgentFileOwnership.Capture(source));
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(destination, name);
            if (IsSymlink(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target, depth + 1);
            }
            else
            {
                CopyFile(entry, target);
            }
        }
    }

    private void AppendTree(string path, StringBuilder builder, int level, int depth, ref int count)
    {
        if (level > depth || count >= _config.MaxListEntries || !Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path).OrderBy(item => item, StringComparer.Ordinal))
        {
            if (count >= _config.MaxListEntries)
            {
                return;
            }

            count++;
            var name = Path.GetFileName(entry);
            builder.AppendLine($"{new string(' ', level * 2)}{name}{(Directory.Exists(entry) ? "/" : string.Empty)}");
            if (Directory.Exists(entry) && !IsSymlink(entry))
            {
                AppendTree(entry, builder, level + 1, depth, ref count);
            }
        }
    }

    private IEnumerable<string> EnumerateFiles(string path, bool recursive, string? filePattern)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            yield return path;
            yield break;
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(path, "*", option))
        {
            if (IsSymlink(file))
            {
                continue;
            }

            if (filePattern is not null
                && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filePattern, Path.GetFileName(file)))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string JobDirectory(string jobId)
    {
        if (jobId.Length == 0 || jobId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("任务编号不合法");
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".remote-fs",
            "jobs",
            jobId);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"找不到任务 {jobId}");
        }

        return directory;
    }

    /// <summary>
    /// 从指定字节偏移读取输出，并按任务记录的编码解码。
    /// 返回的偏移按实际消耗的字节数推进，与 SSH 路径的语义一致，
    /// 否则 GBK 输出会算错位置导致重复读取或漏读。
    /// </summary>
    private static (string Text, long NextOffset) ReadFromOffset(string path, long offset, string encodingName)
    {
        if (!File.Exists(path))
        {
            return (string.Empty, offset);
        }

        byte[] content;
        using (var stream = File.OpenRead(path))
        {
            if (offset >= stream.Length)
            {
                return (string.Empty, offset);
            }

            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            content = buffer.ToArray();
        }

        var decoded = TextEncodingResolver.Decode(content, encodingName, truncated: false);
        var text = decoded.Success
            ? decoded.Text ?? string.Empty
            : decoded.IsBinary
                ? $"[二进制内容，{content.Length} 字节，无法以文本展示]"
                : $"[无法按 {decoded.EncodingName} 解码，{content.Length} 字节]";
        return (text, offset + content.Length);
    }

    private static bool Exists(string path)
    {
        return File.Exists(path) || Directory.Exists(path) || IsSymlink(path);
    }
}
