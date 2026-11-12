using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Mcp;
using RemoteFs.Services;
using RemoteFs.Ssh;

namespace RemoteFs.Agent;

/// <summary>
/// 在服务器本机执行文件工具。SSH 不可用时，Windows 端把同一次工具调用转发到这里。
/// </summary>
internal sealed class AgentToolExecutor
{
    private static readonly string[] SupportedTools =
    [
        "remote_read_file",
        "remote_write_file",
        "remote_edit_file",
        "remote_patch",
        "remote_ls",
        "remote_stat",
        "remote_mkdir",
        "remote_remove"
    ];

    private readonly AppConfig _config;
    private readonly PathGuard _guard;
    private readonly HostProfile _localHost;
    private readonly bool _allowEverything;

    public AgentToolExecutor(AppConfig config)
    {
        _config = config;
        _guard = new PathGuard(config);
        _allowEverything = config.AgentAllowedRoots.Any(root => PathGuard.Normalize(root) == "/");
        _localHost = new HostProfile
        {
            Name = "local",
            AllowedRoots = config.AgentAllowedRoots
        };
    }

    public (bool Ok, string Text) Execute(string tool, JsonObject arguments)
    {
        try
        {
            var context = new ToolCallContext(arguments);
            var text = tool switch
            {
                "remote_read_file" => ReadFile(context),
                "remote_write_file" => WriteFile(context),
                "remote_edit_file" => EditFile(context),
                "remote_patch" => PatchFile(context),
                "remote_ls" => ListDirectory(context),
                "remote_stat" => Stat(context),
                "remote_mkdir" => MakeDirectory(context),
                "remote_remove" => Remove(context),
                _ => throw new InvalidOperationException(
                    "备用通道目前只承接文件读写：" + string.Join("、", SupportedTools) + "。")
            };
            return (true, text);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException
                     or InvalidOperationException or IOException or EncoderFallbackException)
        {
            return (false, exception.Message);
        }
    }

    private string ReadFile(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var offset = context.GetLong("offset") ?? 0;
        var length = context.GetLong("length") ?? -1;
        var maxLines = context.GetInt("maxLines") ?? 0;
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException($"路径 {path} 是目录，请改用 remote_ls");
        }

        var totalLength = info.Length;
        if (offset < 0)
        {
            offset = 0;
        }

        if (offset >= totalLength)
        {
            return $"文件 {path} 为空";
        }

        var effective = length < 0 ? totalLength - offset : Math.Min(length, totalLength - offset);
        effective = Math.Min(effective, _config.MaxReadBytes);
        var buffer = new byte[effective];
        using (var stream = File.OpenRead(path))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != buffer.Length)
            {
                Array.Resize(ref buffer, totalRead);
            }
        }

        var truncated = offset + buffer.Length < totalLength;
        var encodingName = ResolveEncoding(context);
        var decoded = TextEncodingResolver.Decode(buffer, encodingName, truncated);
        if (!decoded.Success)
        {
            throw new InvalidOperationException(decoded.IsBinary
                ? $"文件 {path} 是二进制内容，无法以文本展示（大小 {RemoteFsService.FormatSize(totalLength)}）。"
                : $"文件 {path} 无法按 {decoded.EncodingName} 解码：{decoded.Error}");
        }

        var text = decoded.Text ?? string.Empty;
        var builder = new StringBuilder();
        var host = context.GetString("host") ?? "本机";
        var bomNote = decoded.HasBom ? "，带 BOM" : string.Empty;
        builder.AppendLine(
            $"文件 {path}（主机 {host}，共 {totalLength} 字节，编码 {decoded.EncodingName}{bomNote}）");

        var lines = text.Split('\n');
        var lineLimit = maxLines > 0 ? Math.Min(maxLines, lines.Length) : lines.Length;
        var lineNumberPrefix = offset > 0 ? "~" : string.Empty;
        for (var index = 0; index < lineLimit; index++)
        {
            builder.AppendLine($"{lineNumberPrefix}{index + 1}: {lines[index].TrimEnd('\r')}");
        }

        if (lineLimit < lines.Length)
        {
            builder.AppendLine($"[内容截断，已返回本段的前 {lineLimit} 行，本段共 {lines.Length} 行]");
        }

        if (truncated)
        {
            var nextOffset = offset + buffer.Length;
            builder.AppendLine($"[文件未读完，已读取至偏移 {nextOffset} 字节，请用 offset={nextOffset} 继续读取]");
        }

        return Truncate(builder.ToString());
    }

    private string WriteFile(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: false);
        var content = context.RequireText("content");
        var append = context.GetBool("append");
        var createParents = context.GetBool("createParents", true);
        var permissionsText = context.GetString("permissions");
        var encodingName = ResolveEncoding(context);
        EnsureSupportedEncoding(encodingName);
        if (!string.IsNullOrWhiteSpace(permissionsText) && !TryParseOctal(permissionsText, out _))
        {
            throw new ArgumentException($"权限值 '{permissionsText}' 不是合法的八进制权限，示例：644、755");
        }

        if (createParents)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        var existed = File.Exists(path);
        if (!existed && append)
        {
            append = false;
        }

        var snapshot = existed && !append ? AgentFileOwnership.Capture(path) : default;
        byte[] payload;
        try
        {
            payload = TextEncodingResolver.EncodeBytes(content, TextEncodingResolver.ResolveForWrite(encodingName), false);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidOperationException(
                $"无法用 {encodingName} 编码写入文件 {path}：文本中包含该编码无法表示的字符。");
        }

        if (append)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(payload, 0, payload.Length);
        }
        else
        {
            AtomicWrite(path, payload, snapshot);
        }

        if (!string.IsNullOrWhiteSpace(permissionsText) && TryParseOctal(permissionsText, out var mode))
        {
            AgentFileOwnership.ApplyMode(path, mode);
        }

        var size = new FileInfo(path).Length;
        var action = existed ? (append ? "追加" : "覆盖") : "创建";
        return $"已{action}文件 {path}，当前大小 {size} 字节";
    }

    private string EditFile(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var oldText = context.RequireString("oldText");
        var newText = context.RequireText("newText");
        var replaceAll = context.GetBool("replaceAll");
        var length = new FileInfo(path).Length;
        if (length > _config.MaxReadBytes)
        {
            throw new InvalidOperationException(
                $"文件 {path} 大小 {length} 字节，超过单次读取上限 {_config.MaxReadBytes} 字节。"
                + "此时整体写回会丢失未读取的内容，已拒绝编辑。");
        }

        var bytes = File.ReadAllBytes(path);
        var encodingName = ResolveEncoding(context);
        var decoded = TextEncodingResolver.Decode(bytes, encodingName, truncated: false);
        if (!decoded.Success)
        {
            throw new InvalidOperationException(decoded.IsBinary
                ? $"文件 {path} 是二进制内容，无法做文本替换"
                : $"文件 {path} 无法按 {decoded.EncodingName} 解码：{decoded.Error}");
        }

        var text = decoded.Text ?? string.Empty;
        var occurrences = CountOccurrences(text, oldText);
        if (occurrences == 0)
        {
            var preview = text.Length > 2000 ? text[..2000] + "\n[文件内容较长，已截断]" : text;
            throw new InvalidOperationException($"在文件 {path} 中未找到要替换的文本。文件当前内容：\n{preview}");
        }

        if (occurrences > 1 && !replaceAll)
        {
            throw new InvalidOperationException(
                $"在文件 {path} 中找到 {occurrences} 处相同文本。请提供更精确的 oldText，或设置 replaceAll=true。");
        }

        var updated = replaceAll
            ? text.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(text, oldText, newText);
        var (rewriteEncoding, writeBom) = TextEncodingResolver.ResolveForRewrite(decoded.EncodingName, decoded.HasBom);
        byte[] payload;
        try
        {
            payload = TextEncodingResolver.EncodeBytes(updated, rewriteEncoding, writeBom);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidOperationException($"无法用 {decoded.EncodingName} 编码写回文件 {path}。");
        }

        AtomicWrite(path, payload, AgentFileOwnership.Capture(path));
        var replacedCount = replaceAll ? occurrences : 1;
        return $"已编辑文件 {path}，替换 {replacedCount} 处";
    }

    private string PatchFile(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var startLine = context.GetInt("startLine") ?? 0;
        var endLine = context.GetInt("endLine") ?? startLine;
        var content = context.RequireText("content");
        if (startLine < 1)
        {
            throw new ArgumentException("startLine 必须从 1 开始");
        }

        if (endLine < startLine)
        {
            throw new ArgumentException($"endLine({endLine}) 不能小于 startLine({startLine})");
        }

        var encodingName = ResolveEncoding(context);
        EnsureSupportedEncoding(encodingName);
        byte[] payload;
        try
        {
            payload = TextEncodingResolver.ResolveForWrite(encodingName).GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidOperationException($"无法用 {encodingName} 编码替换内容。");
        }

        payload = EnsureLineEnding(payload);
        var total = AgentFileSplicer.CountLines(path);
        if (total < startLine)
        {
            throw new InvalidOperationException($"文件 {path} 只有 {total} 行，没有第 {startLine} 行。");
        }

        var snapshot = AgentFileOwnership.Capture(path);
        var directory = Path.GetDirectoryName(path) ?? "/";
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");
        try
        {
            AgentFileSplicer.Splice(path, temporary, startLine, endLine, payload);
            File.Move(temporary, path, overwrite: true);
            AgentFileOwnership.Restore(path, snapshot);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        var writtenLines = CountLines(payload);
        return content.Length == 0
            ? $"已删除 {path} 的第 {startLine}-{endLine} 行"
            : $"已将 {path} 的第 {startLine}-{endLine} 行替换为 {writtenLines} 行";
    }

    private string ListDirectory(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var showHidden = context.GetBool("showHidden");
        if (!Directory.Exists(path))
        {
            var file = new FileInfo(path);
            return Describe(file.FullName, file.Length, "文件", file.LastWriteTimeUtc);
        }

        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(item => new FileInfo(item))
            .Where(item => showHidden || !item.Name.StartsWith('.'))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
        var visible = entries.Take(_config.MaxListEntries).ToList();
        var builder = new StringBuilder();
        foreach (var entry in visible)
        {
            var type = entry.Attributes.HasFlag(FileAttributes.Directory) ? "目录" : "文件";
            var size = type == "目录" ? 0 : entry.Length;
            builder.AppendLine(Describe(entry.FullName, size, type, entry.LastWriteTimeUtc));
        }

        if (visible.Count < entries.Count)
        {
            builder.AppendLine($"[条目过多，仅显示前 {visible.Count} 项，还有 {entries.Count - visible.Count} 项未展示]");
        }

        return Truncate(builder.Length == 0 ? $"目录 {path} 为空" : builder.ToString());
    }

    private string Stat(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: true);
        var directory = Directory.Exists(path);
        var info = new FileInfo(path);
        var type = IsSymlink(path) ? "链接" : directory ? "目录" : "文件";
        var builder = new StringBuilder();
        builder.AppendLine($"路径: {path}");
        builder.AppendLine($"类型: {type}");
        builder.AppendLine($"大小: {(directory ? "-" : RemoteFsService.FormatSize(info.Length))}");
        builder.AppendLine($"修改时间: {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}");
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                builder.AppendLine($"权限: {Convert.ToString((int)File.GetUnixFileMode(path), 8)}");
            }
            catch (Exception)
            {
                // 权限读不到时省略这一行
            }
        }

        return builder.ToString().TrimEnd();
    }

    private string MakeDirectory(ToolCallContext context)
    {
        var path = Prepare(context.RequireString("path"), mustExist: false);
        var createParents = context.GetBool("createParents", true);
        if (Directory.Exists(path))
        {
            return $"目录 {path} 已存在";
        }

        if (File.Exists(path))
        {
            throw new IOException($"路径 {path} 已存在并且不是目录");
        }

        var parent = Path.GetDirectoryName(path);
        if (!createParents
            && !string.IsNullOrEmpty(parent)
            && parent != "/"
            && !Directory.Exists(parent))
        {
            throw new IOException($"父目录 {parent} 不存在");
        }

        Directory.CreateDirectory(path);
        return $"已创建目录 {path}";
    }

    private string Remove(ToolCallContext context)
    {
        var raw = context.RequireString("path");
        var path = Prepare(raw, mustExist: false);
        var recursive = context.GetBool("recursive");
        if (!File.Exists(path) && !Directory.Exists(path) && !IsSymlink(path))
        {
            return $"路径 {path} 不存在，无需删除";
        }

        if (IsSymlink(path))
        {
            File.Delete(path);
            return $"已删除 {path}";
        }

        if (Directory.Exists(path))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new InvalidOperationException($"目录 {path} 非空。请设置 recursive=true 递归删除。");
            }

            Directory.Delete(path, recursive);
            return $"已删除 {path}";
        }

        File.Delete(path);
        return $"已删除 {path}";
    }

    /// <summary>
    /// 校验绝对路径、白名单和软链接，返回本机可用的路径。
    /// </summary>
    private string Prepare(string path, bool mustExist)
    {
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException($"路径必须是绝对路径: {path}");
        }

        var normalized = PathGuard.Normalize(path);
        if (!_allowEverything)
        {
            _guard.EnsureAllowed(_localHost, normalized);
        }

        if (!_config.FollowSymbolicLinks && IsSymlink(normalized))
        {
            throw PathGuard.BuildSymbolicLinkRejection(_localHost, normalized);
        }

        if (mustExist && !File.Exists(normalized) && !Directory.Exists(normalized))
        {
            throw new FileNotFoundException($"路径 {normalized} 不存在", normalized);
        }

        return normalized;
    }

    private string ResolveEncoding(ToolCallContext context)
    {
        var explicitName = context.GetString("encoding");
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return TextEncodingResolver.Normalize(explicitName);
        }

        return TextEncodingResolver.Normalize(_config.DefaultEncoding);
    }

    private static void EnsureSupportedEncoding(string encodingName)
    {
        if (!TextEncodingResolver.IsSupported(encodingName))
        {
            throw new ArgumentException(
                $"不支持的编码 \"{encodingName}\"。可用值："
                + string.Join("、", TextEncodingResolver.SupportedNames));
        }
    }

    private void AtomicWrite(string path, byte[] payload, AgentFileOwnership.Snapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException($"无法确定 {path} 的目录");
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, path, overwrite: true);
            AgentFileOwnership.Restore(path, snapshot);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string Truncate(string text)
    {
        if (text.Length <= _config.MaxOutputCharacters)
        {
            return text;
        }

        return text[.._config.MaxOutputCharacters] + "\n[输出已截断]";
    }

    private static string Describe(string path, long size, string type, DateTime modifiedUtc)
    {
        var shown = type == "目录" ? "-" : RemoteFsService.FormatSize(size);
        return $"{type,-4} {shown,10}  {modifiedUtc:yyyy-MM-dd HH:mm:ss}  {path}";
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParseOctal(string text, out int mode)
    {
        mode = 0;
        if (text.Length is 0 or > 4)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '7')
            {
                return false;
            }

            mode = (mode << 3) + (character - '0');
        }

        return mode <= 0xFFF;
    }

    private static byte[] EnsureLineEnding(byte[] content)
    {
        if (content.Length == 0 || content[^1] == (byte)'\n')
        {
            return content;
        }

        var payload = new byte[content.Length + 1];
        content.CopyTo(payload, 0);
        payload[^1] = (byte)'\n';
        return payload;
    }

    private static int CountLines(byte[] content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var value in content)
        {
            if (value == (byte)'\n')
            {
                count++;
            }
        }

        return content[^1] == (byte)'\n' ? count : count + 1;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += Math.Max(pattern.Length, 1);
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        return text[..index] + newText + text[(index + oldText.Length)..];
    }

    public static bool TokenMatches(string expected, string provided)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
