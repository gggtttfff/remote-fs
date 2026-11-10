using System.Text;
using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Mcp;
using RemoteFs.Services;
using RemoteFs.Ssh;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace RemoteFs.Tools;

/// <summary>
/// 文件读取类工具：列出目录、读取文件、查看文件属性、搜索内容、浏览目录树。
/// </summary>
public sealed class FileReadTools
{
    private readonly RemoteFsService _service;

    public FileReadTools(RemoteFsService service)
    {
        _service = service;
    }

    /// <summary>
    /// 构建全部只读文件工具
    /// </summary>
    public List<ToolDefinition> Build()
    {
        return new List<ToolDefinition>
        {
            BuildListDirectory(),
            BuildReadFile(),
            BuildStatFile(),
            BuildSearchContent(),
            BuildTree()
        };
    }

    /// <summary>
    /// 列出远程目录内容
    /// </summary>
    private ToolDefinition BuildListDirectory()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("要列出的远程目录绝对路径，如 /etc/nginx")),
                ("showHidden", SchemaBuilder.Boolean("是否显示以 . 开头的隐藏项", false))
            },
            new[] { "path" });

        return new ToolDefinition
        {
            Name = "remote_ls",
            Description = "列出远程主机上的目录内容，返回文件名、类型、大小、修改时间和权限。"
                + "用于查看远程服务器目录结构，替代人工 SSH 执行 ls。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var showHidden = context.GetBool("showHidden");

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);
                var attributes = sftp.GetAttributes(path);

                if (!attributes.IsDirectory)
                {
                    return ToolResult.Ok(DescribeFile(path, attributes));
                }

                var entries = sftp.ListDirectory(path)
                    .Where(entry => showHidden || !entry.Name.StartsWith('.'))
                    .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                    .ToList();

                if (entries.Count == 0)
                {
                    return ToolResult.Ok($"目录 {path} 为空");
                }

                var builder = new StringBuilder();
                builder.AppendLine($"目录 {path}（主机 {profile.Name}）共 {entries.Count} 项：");
                foreach (var entry in entries)
                {
                    builder.AppendLine(DescribeFile(PathGuard.Combine(path, entry.Name), entry));
                }
                return ToolResult.Ok(_service.TruncateOutput(builder.ToString()));
            }
        };
    }

    /// <summary>
    /// 读取远程文件内容，支持分页
    /// </summary>
    private ToolDefinition BuildReadFile()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("远程文件绝对路径")),
                ("offset", SchemaBuilder.Integer("起始字节偏移，用于分页读取大文件", 0)),
                ("length", SchemaBuilder.Integer("读取字节数，-1 表示读取到文件末尾", -1)),
                ("maxLines", SchemaBuilder.Integer("最多返回的行数，超出部分截断并提示继续读取的行号", 0))
            },
            new[] { "path" });

        return new ToolDefinition
        {
            Name = "remote_read_file",
            Description = "读取远程主机上的文件内容，返回带行号的文本。"
                + "大文件会自动分页，通过 offset/length 继续读取。二进制文件请改用 remote_download。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var offset = context.GetLong("offset") ?? 0;
                var length = context.GetLong("length") ?? -1;
                var maxLines = context.GetInt("maxLines") ?? 0;

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                var (content, totalLength, truncated) = RemoteFileReader.Read(
                    sftp, path, offset, length, _service.Config.MaxReadBytes);

                if (content.Length == 0 && totalLength == 0)
                {
                    return ToolResult.Ok($"文件 {path} 为空");
                }

                var text = RemoteFileReader.TryDecodeText(content);
                if (text is null)
                {
                    return ToolResult.Error(
                        $"文件 {path} 是二进制内容，无法以文本展示（大小 {RemoteFsService.FormatSize(totalLength)}）。"
                        + "请改用 remote_download 工具下载到本地处理。");
                }

                var builder = new StringBuilder();
                builder.AppendLine($"文件 {path}（主机 {profile.Name}，共 {totalLength} 字节）");

                var lines = text.Split('\n');
                var lineLimit = maxLines > 0 ? Math.Min(maxLines, lines.Length) : lines.Length;

                // 按偏移分段读取时行号无法从 1 起算，此处明确标注
                var lineNumberPrefix = offset > 0 ? "~" : string.Empty;

                for (var index = 0; index < lineLimit; index++)
                {
                    builder.AppendLine($"{lineNumberPrefix}{index + 1}: {lines[index].TrimEnd('\r')}");
                }

                if (lineLimit < lines.Length)
                {
                    builder.AppendLine($"[内容截断，已返回前 {lineLimit} 行，共 {lines.Length} 行]");
                }
                if (truncated)
                {
                    builder.AppendLine(
                        $"[文件未读完，已读取至偏移 {offset + content.Length} 字节，" +
                        $"请用 offset={offset + content.Length} 继续读取]");
                }

                return ToolResult.Ok(_service.TruncateOutput(builder.ToString()));
            }
        };
    }

    /// <summary>
    /// 查看文件或目录属性
    /// </summary>
    private ToolDefinition BuildStatFile()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("远程文件或目录绝对路径"))
            },
            new[] { "path" });

        return new ToolDefinition
        {
            Name = "remote_stat",
            Description = "查看远程文件或目录的详细属性：类型、大小、权限、属主属组、修改时间。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);
                var attributes = sftp.GetAttributes(path);

                var builder = new StringBuilder();
                builder.AppendLine($"路径: {path}");
                builder.AppendLine($"主机: {profile.Name}");
                builder.AppendLine($"类型: {(attributes.IsDirectory ? "目录" : attributes.IsSymbolicLink ? "符号链接" : "文件")}");
                builder.AppendLine($"大小: {attributes.Size} 字节 ({RemoteFsService.FormatSize(attributes.Size)})");
                builder.AppendLine($"权限: {FormatPermissions(attributes)}");
                builder.AppendLine($"属主: {attributes.UserId}");
                builder.AppendLine($"属组: {attributes.GroupId}");
                builder.AppendLine($"修改时间: {attributes.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                builder.AppendLine($"访问时间: {attributes.LastAccessTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                if (attributes.IsSymbolicLink)
                {
                    builder.AppendLine("链接目标: 请用 remote_exec 执行 readlink 查看");
                }

                return ToolResult.Ok(builder.ToString());
            }
        };
    }

    /// <summary>
    /// 在远程目录中搜索内容
    /// </summary>
    private ToolDefinition BuildSearchContent()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("要搜索的远程目录绝对路径")),
                ("pattern", SchemaBuilder.String("要搜索的文本内容")),
                ("filePattern", SchemaBuilder.String("文件名过滤的通配符，如 *.conf，默认匹配所有文件")),
                ("caseSensitive", SchemaBuilder.Boolean("是否区分大小写", false)),
                ("maxResults", SchemaBuilder.Integer("最大返回结果条数", 50)),
                ("recursive", SchemaBuilder.Boolean("是否递归搜索子目录", true))
            },
            new[] { "path", "pattern" });

        return new ToolDefinition
        {
            Name = "remote_search",
            Description = "在远程主机的目录中搜索包含指定文本的文件，返回匹配的文件路径与行内容。"
                + "用于快速定位配置文件中的配置项，比逐个读取文件更高效。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var pattern = context.RequireString("pattern");
                var filePattern = context.GetString("filePattern");
                var caseSensitive = context.GetBool("caseSensitive");
                var maxResults = context.GetInt("maxResults") ?? 50;
                var recursive = context.GetBool("recursive", true);

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                var comparison = caseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                var builder = new StringBuilder();
                var matchCount = 0;
                var scannedCount = 0;

                foreach (var file in EnumerateFiles(sftp, path, recursive, filePattern))
                {
                    scannedCount++;
                    if (matchCount >= maxResults)
                    {
                        break;
                    }

                    try
                    {
                        var (content, _, _) = RemoteFileReader.Read(sftp, file, 0, -1, _service.Config.MaxReadBytes);
                        var text = RemoteFileReader.TryDecodeText(content);
                        if (text is null)
                        {
                            continue;
                        }

                        var lines = text.Split('\n');
                        for (var index = 0; index < lines.Length && matchCount < maxResults; index++)
                        {
                            if (lines[index].Contains(pattern, comparison))
                            {
                                builder.AppendLine($"{file}:{index + 1}: {lines[index].TrimEnd('\r').Trim()}");
                                matchCount++;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        AuditLogger.Warn($"搜索跳过文件 {file}: {exception.Message}");
                    }
                }

                if (matchCount == 0)
                {
                    return ToolResult.Ok($"在 {path} 中未找到包含 \"{pattern}\" 的内容（已扫描 {scannedCount} 个文件）");
                }

                builder.Insert(0, $"在 {path} 中找到 {matchCount} 处匹配（已扫描 {scannedCount} 个文件）：\n");
                return ToolResult.Ok(_service.TruncateOutput(builder.ToString()));
            }
        };
    }

    /// <summary>
    /// 浏览目录树结构
    /// </summary>
    private ToolDefinition BuildTree()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("起始目录绝对路径")),
                ("depth", SchemaBuilder.Integer("递归深度，默认 2", 1, 10))
            },
            new[] { "path" });

        return new ToolDefinition
        {
            Name = "remote_tree",
            Description = "以树形结构展示远程目录结构，便于快速了解服务器上目录布局。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var depth = context.GetInt("depth") ?? 2;

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                var builder = new StringBuilder();
                builder.AppendLine($"{path}（主机 {profile.Name}）");
                var entryCount = 0;
                var maxEntries = _service.Config.MaxListEntries;

                AppendTree(sftp, path, builder, 1, depth, ref entryCount, maxEntries);

                if (entryCount >= maxEntries)
                {
                    builder.AppendLine($"[条目数达到上限 {maxEntries}，未继续展开]");
                }

                return ToolResult.Ok(_service.TruncateOutput(builder.ToString()));
            }
        };
    }

    /// <summary>
    /// 递归追加目录树
    /// </summary>
    private void AppendTree(
        SftpClient sftp, string directory, StringBuilder builder, int level, int depth,
        ref int entryCount, int maxEntries)
    {
        if (level > depth || entryCount >= maxEntries)
        {
            return;
        }

        List<ISftpFile> entries;
        try
        {
            entries = sftp.ListDirectory(directory)
                .Where(entry => !entry.Name.StartsWith('.'))
                .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception)
        {
            builder.AppendLine($"{new string(' ', level * 2)}[无法读取: {exception.Message}]");
            return;
        }

        foreach (var entry in entries)
        {
            if (entryCount >= maxEntries)
            {
                return;
            }
            entryCount++;

            var indent = new string(' ', level * 2);
            if (entry.IsDirectory)
            {
                builder.AppendLine($"{indent}{entry.Name}/");
                AppendTree(sftp, entry.FullName, builder, level + 1, depth, ref entryCount, maxEntries);
            }
            else
            {
                builder.AppendLine($"{indent}{entry.Name}  ({RemoteFsService.FormatSize(entry.Length)})");
            }
        }
    }

    /// <summary>
    /// 枚举目录下的文件
    /// </summary>
    private IEnumerable<string> EnumerateFiles(
        SftpClient sftp, string directory, bool recursive, string? filePattern)
    {
        List<ISftpFile> entries;
        try
        {
            entries = sftp.ListDirectory(directory).ToList();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取目录 {directory} 失败: {exception.Message}");
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                if (recursive)
                {
                    foreach (var nested in EnumerateFiles(sftp, entry.FullName, recursive, filePattern))
                    {
                        yield return nested;
                    }
                }
                continue;
            }

            if (!entry.IsRegularFile)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(filePattern) && !MatchesPattern(entry.Name, filePattern))
            {
                continue;
            }

            yield return entry.FullName;
        }
    }

    /// <summary>
    /// 通配符匹配
    /// </summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 描述文件条目
    /// </summary>
    private static string DescribeFile(string path, ISftpFile file)
    {
        var type = file.IsDirectory ? "目录" : file.IsSymbolicLink ? "链接" : "文件";
        var size = file.IsDirectory ? "-" : RemoteFsService.FormatSize(file.Length);
        var modified = file.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss");
        return $"{type,-4} {size,10}  {modified}  {path}";
    }

    /// <summary>
    /// 描述文件属性
    /// </summary>
    private static string DescribeFile(string path, SftpFileAttributes attributes)
    {
        var type = attributes.IsDirectory ? "目录" : attributes.IsSymbolicLink ? "链接" : "文件";
        var size = attributes.IsDirectory ? "-" : RemoteFsService.FormatSize(attributes.Size);
        var modified = attributes.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss");
        return $"{type,-4} {size,10}  {modified}  {path}";
    }

    /// <summary>
    /// 格式化权限位
    /// </summary>
    private static string FormatPermissions(SftpFileAttributes attributes)
    {
        return string.Create(9, attributes, (span, value) =>
        {
            span[0] = value.OwnerCanRead ? 'r' : '-';
            span[1] = value.OwnerCanWrite ? 'w' : '-';
            span[2] = value.OwnerCanExecute ? 'x' : '-';
            span[3] = value.GroupCanRead ? 'r' : '-';
            span[4] = value.GroupCanWrite ? 'w' : '-';
            span[5] = value.GroupCanExecute ? 'x' : '-';
            span[6] = value.OthersCanRead ? 'r' : '-';
            span[7] = value.OthersCanWrite ? 'w' : '-';
            span[8] = value.OthersCanExecute ? 'x' : '-';
        });
    }
}
