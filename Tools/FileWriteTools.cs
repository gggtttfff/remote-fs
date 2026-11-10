using System.Text;
using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Mcp;
using RemoteFs.Services;
using RemoteFs.Ssh;
using Renci.SshNet;

namespace RemoteFs.Tools;

/// <summary>
/// 文件写入与目录操作工具：写文件、编辑文件、创建目录、删除、移动、复制、改权限。
/// </summary>
public sealed class FileWriteTools
{
    private readonly RemoteFsService _service;

    public FileWriteTools(RemoteFsService service)
    {
        _service = service;
    }

    /// <summary>
    /// 构建全部写操作工具
    /// </summary>
    public List<ToolDefinition> Build()
    {
        return new List<ToolDefinition>
        {
            BuildWriteFile(),
            BuildEditFile(),
            BuildMakeDirectory(),
            BuildRemove(),
            BuildMove(),
            BuildCopy(),
            BuildChmod(),
            BuildCreateLink()
        };
    }

    /// <summary>
    /// 写入文件内容
    /// </summary>
    private ToolDefinition BuildWriteFile()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("远程文件绝对路径")),
                ("content", SchemaBuilder.String("要写入的文本内容")),
                ("append", SchemaBuilder.Boolean("是否追加到文件末尾，默认覆盖", false)),
                ("createParents", SchemaBuilder.Boolean("父目录不存在时是否自动创建", true)),
                ("permissions", SchemaBuilder.String("文件权限位，如 644，默认保留原有权限"))
            },
            new[] { "path", "content" });

        return new ToolDefinition
        {
            Name = "remote_write_file",
            Description = "将文本内容写入远程文件。默认覆盖整个文件，append=true 时追加到末尾。"
                + "内容原样写入，不做 shell 转义，适合写配置文件。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var content = context.RequireString("content");
                var append = context.GetBool("append");
                var createParents = context.GetBool("createParents", true);
                var permissions = context.GetString("permissions");

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                if (createParents)
                {
                    var parent = RemoteFileWriter.GetParentDirectory(path);
                    RemoteFileWriter.EnsureDirectory(sftp, parent);
                }

                var existed = sftp.Exists(path);
                if (!existed && append)
                {
                    // 追加模式但文件不存在，等价于创建
                    append = false;
                }

                RemoteFileWriter.WriteText(sftp, path, content, append);

                // 写入后按需调整权限位，权限值按八进制解析（如 644）
                if (!string.IsNullOrWhiteSpace(permissions))
                {
                    if (!TryParseOctalPermissions(permissions, out var mode))
                    {
                        return ToolResult.Error($"权限值 '{permissions}' 不是合法的八进制权限，示例：644、755");
                    }
                    sftp.ChangePermissions(path, mode);
                }

                var size = sftp.GetAttributes(path).Size;
                AuditLogger.Info($"写入文件 [{profile.Name}] {path} ({size} 字节, append={append})");

                var action = existed ? (append ? "追加" : "覆盖") : "创建";
                return ToolResult.Ok($"已{action}文件 {path}（主机 {profile.Name}），当前大小 {size} 字节");
            }
        };
    }

    /// <summary>
    /// 精确编辑文件中的指定文本
    /// </summary>
    private ToolDefinition BuildEditFile()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("远程文件绝对路径")),
                ("oldText", SchemaBuilder.String("要替换的原文本，必须精确匹配（含缩进与换行）")),
                ("newText", SchemaBuilder.String("替换后的新文本")),
                ("replaceAll", SchemaBuilder.Boolean("是否替换所有匹配项，默认只替换第一处", false))
            },
            new[] { "path", "oldText", "newText" });

        return new ToolDefinition
        {
            Name = "remote_edit_file",
            Description = "在远程文件中替换指定文本。比整体重写文件更安全，不会误删未读到的内容。"
                + "oldText 需精确匹配，匹配不到会报错并返回文件内容片段供确认。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var oldText = context.RequireString("oldText");
                var newText = context.GetString("newText") ?? string.Empty;
                var replaceAll = context.GetBool("replaceAll");

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                var (content, _, _) = RemoteFileReader.Read(sftp, path, 0, -1, _service.Config.MaxReadBytes);
                var text = RemoteFileReader.TryDecodeText(content)
                    ?? throw new InvalidOperationException($"文件 {path} 是二进制内容，无法做文本替换");

                var comparison = StringComparison.Ordinal;
                var occurrences = CountOccurrences(text, oldText, comparison);
                if (occurrences == 0)
                {
                    // 兜底：返回文件片段帮助模型定位问题
                    var preview = text.Length > 2000 ? text[..2000] + "\n[文件内容较长，已截断]" : text;
                    return ToolResult.Error(
                        $"在文件 {path} 中未找到要替换的文本。文件当前内容：\n{preview}");
                }

                if (occurrences > 1 && !replaceAll)
                {
                    return ToolResult.Error(
                        $"在文件 {path} 中找到 {occurrences} 处相同文本。请提供更精确的 oldText（包含上下文），"
                        + "或设置 replaceAll=true 全部替换。");
                }

                var updated = replaceAll
                    ? text.Replace(oldText, newText, StringComparison.Ordinal)
                    : ReplaceFirst(text, oldText, newText);

                // 原子写入：先写临时文件再重命名，避免写到一半失败损坏原文件
                RemoteFileWriter.WriteTextAtomic(sftp, path, updated);

                var replacedCount = replaceAll ? occurrences : 1;
                AuditLogger.Info($"编辑文件 [{profile.Name}] {path}，替换 {replacedCount} 处");
                return ToolResult.Ok($"已编辑文件 {path}（主机 {profile.Name}），替换 {replacedCount} 处");
            }
        };
    }

    /// <summary>
    /// 创建目录
    /// </summary>
    private ToolDefinition BuildMakeDirectory()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("要创建的远程目录绝对路径")),
                ("createParents", SchemaBuilder.Boolean("是否递归创建父目录", true))
            },
            new[] { "path" });

        return new ToolDefinition
        {
            Name = "remote_mkdir",
            Description = "在远程主机上创建目录，可递归创建多级目录。目录已存在时不报错。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var createParents = context.GetBool("createParents", true);

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                if (sftp.Exists(path))
                {
                    return ToolResult.Ok($"目录 {path} 已存在");
                }

                if (createParents)
                {
                    RemoteFileWriter.EnsureDirectory(sftp, path);
                }
                else
                {
                    sftp.CreateDirectory(path);
                }

                AuditLogger.Info($"创建目录 [{profile.Name}] {path}");
                return ToolResult.Ok($"已创建目录 {path}（主机 {profile.Name}）");
            }
        };
    }

    /// <summary>
    /// 删除文件或目录
    /// </summary>
    private ToolDefinition BuildRemove()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("要删除的远程文件或目录绝对路径")),
                ("recursive", SchemaBuilder.Boolean("删除目录时是否递归删除其内容", false))
            },
            new[] { "path" });

        return new ToolDefinition
        {
            Name = "remote_remove",
            Description = "删除远程文件或目录。删除目录需显式设置 recursive=true，否则非空目录会失败。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var recursive = context.GetBool("recursive");

                _service.EnsurePathAllowed(profile, path);

                using var sftp = _service.OpenSftp(profile);

                if (!sftp.Exists(path))
                {
                    return ToolResult.Ok($"路径 {path} 不存在，无需删除");
                }

                var attributes = sftp.GetAttributes(path);
                if (attributes.IsDirectory)
                {
                    if (!recursive)
                    {
                        var entries = sftp.ListDirectory(path).ToList();
                        if (entries.Count > 0)
                        {
                            return ToolResult.Error(
                                $"目录 {path} 非空（{entries.Count} 项）。请设置 recursive=true 递归删除。");
                        }
                    }
                    DeleteDirectoryRecursive(sftp, path);
                }
                else
                {
                    sftp.DeleteFile(path);
                }

                AuditLogger.Info($"删除路径 [{profile.Name}] {path} (recursive={recursive})");
                return ToolResult.Ok($"已删除 {path}（主机 {profile.Name}）");
            }
        };
    }

    /// <summary>
    /// 移动或重命名
    /// </summary>
    private ToolDefinition BuildMove()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("source", SchemaBuilder.String("源路径绝对路径")),
                ("destination", SchemaBuilder.String("目标路径绝对路径"))
            },
            new[] { "source", "destination" });

        return new ToolDefinition
        {
            Name = "remote_move",
            Description = "移动或重命名远程文件/目录。目标已存在时覆盖。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var source = context.RequireString("source");
                var destination = context.RequireString("destination");

                _service.EnsurePathAllowed(profile, source);
                _service.EnsurePathAllowed(profile, destination);

                using var sftp = _service.OpenSftp(profile);

                if (!sftp.Exists(source))
                {
                    return ToolResult.Error($"源路径 {source} 不存在");
                }
                sftp.RenameFile(source, destination, true);
                AuditLogger.Info($"移动路径 [{profile.Name}] {source} -> {destination}");
                return ToolResult.Ok($"已将 {source} 移动到 {destination}（主机 {profile.Name}）");
            }
        };
    }

    /// <summary>
    /// 复制文件或目录
    /// </summary>
    private ToolDefinition BuildCopy()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("source", SchemaBuilder.String("源路径绝对路径")),
                ("destination", SchemaBuilder.String("目标路径绝对路径")),
                ("recursive", SchemaBuilder.Boolean("复制目录时是否递归复制内容", true))
            },
            new[] { "source", "destination" });

        return new ToolDefinition
        {
            Name = "remote_copy",
            Description = "复制远程文件或目录到另一位置。目录复制默认递归。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var source = context.RequireString("source");
                var destination = context.RequireString("destination");
                var recursive = context.GetBool("recursive", true);

                _service.EnsurePathAllowed(profile, source);
                _service.EnsurePathAllowed(profile, destination);

                using var sftp = _service.OpenSftp(profile);

                if (!sftp.Exists(source))
                {
                    return ToolResult.Error($"源路径 {source} 不存在");
                }

                var attributes = sftp.GetAttributes(source);
                if (attributes.IsDirectory)
                {
                    if (!recursive)
                    {
                        return ToolResult.Error($"源路径 {source} 是目录，需要 recursive=true 才能复制");
                    }
                    CopyDirectory(sftp, source, destination);
                }
                else
                {
                    var parent = RemoteFileWriter.GetParentDirectory(destination);
                    RemoteFileWriter.EnsureDirectory(sftp, parent);
                    using (var output = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        sftp.DownloadFile(source, output, null);
                    }
                }

                AuditLogger.Info($"复制路径 [{profile.Name}] {source} -> {destination}");
                return ToolResult.Ok($"已将 {source} 复制到 {destination}（主机 {profile.Name}）");
            }
        };
    }

    /// <summary>
    /// 修改权限
    /// </summary>
    private ToolDefinition BuildChmod()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("远程文件或目录绝对路径")),
                ("permissions", SchemaBuilder.String("权限位，如 644、755、600"))
            },
            new[] { "path", "permissions" });

        return new ToolDefinition
        {
            Name = "remote_chmod",
            Description = "修改远程文件或目录的权限位，使用八进制表示，如 644。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var permissionsText = context.RequireString("permissions");

                _service.EnsurePathAllowed(profile, path);

                if (!TryParseOctalPermissions(permissionsText, out var permissions))
                {
                    return ToolResult.Error($"权限值 '{permissionsText}' 不是合法的八进制权限，示例：644、755");
                }

                using var sftp = _service.OpenSftp(profile);

                if (!sftp.Exists(path))
                {
                    return ToolResult.Error($"路径 {path} 不存在");
                }

                sftp.ChangePermissions(path, permissions);
                AuditLogger.Info($"修改权限 [{profile.Name}] {path} -> {permissions:o}");
                return ToolResult.Ok($"已将 {path}（主机 {profile.Name}）权限修改为 {permissions:o}");
            }
        };
    }

    /// <summary>
    /// 创建符号链接
    /// </summary>
    private ToolDefinition BuildCreateLink()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("target", SchemaBuilder.String("链接指向的原始路径")),
                ("linkPath", SchemaBuilder.String("要创建的符号链接路径"))
            },
            new[] { "target", "linkPath" });

        return new ToolDefinition
        {
            Name = "remote_symlink",
            Description = "在远程主机上创建符号链接（软链接）。",
            InputSchema = schema,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var target = context.RequireString("target");
                var linkPath = context.RequireString("linkPath");

                _service.EnsurePathAllowed(profile, target);
                _service.EnsurePathAllowed(profile, linkPath);

                using var sftp = _service.OpenSftp(profile);

                if (sftp.Exists(linkPath))
                {
                    return ToolResult.Error($"链接路径 {linkPath} 已存在");
                }

                var parent = RemoteFileWriter.GetParentDirectory(linkPath);
                RemoteFileWriter.EnsureDirectory(sftp, parent);
                sftp.SymbolicLink(target, linkPath);

                AuditLogger.Info($"创建链接 [{profile.Name}] {linkPath} -> {target}");
                return ToolResult.Ok($"已创建符号链接 {linkPath} -> {target}（主机 {profile.Name}）");
            }
        };
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private static void CopyDirectory(SftpClient sftp, string source, string destination)
    {
        RemoteFileWriter.EnsureDirectory(sftp, destination);

        foreach (var entry in sftp.ListDirectory(source))
        {
            var target = PathGuard.Combine(destination, entry.Name);
            if (entry.IsDirectory)
            {
                CopyDirectory(sftp, entry.FullName, target);
            }
            else if (entry.IsRegularFile)
            {
                using var output = File.Open(target, FileMode.Create, FileAccess.Write, FileShare.None);
                sftp.DownloadFile(entry.FullName, output, null);
            }
        }
    }

    /// <summary>
    /// 按八进制解析权限位，如 644
    /// </summary>
    private static bool TryParseOctalPermissions(string text, out short mode)
    {
        mode = 0;
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 4)
        {
            return false;
        }
        if (!trimmed.All(character => character is >= '0' and <= '7'))
        {
            return false;
        }
        try
        {
            mode = Convert.ToInt16(trimmed, 8);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 递归删除目录
    /// </summary>
    private static void DeleteDirectoryRecursive(SftpClient sftp, string directory)
    {
        foreach (var entry in sftp.ListDirectory(directory))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }
            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursive(sftp, entry.FullName);
            }
            else
            {
                sftp.DeleteFile(entry.FullName);
            }
        }
        sftp.DeleteDirectory(directory);
    }

    /// <summary>
    /// 统计文本出现次数
    /// </summary>
    private static int CountOccurrences(string text, string pattern, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, comparison)) >= 0)
        {
            count++;
            index += Math.Max(pattern.Length, 1);
        }
        return count;
    }

    /// <summary>
    /// 替换第一处匹配
    /// </summary>
    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }
        return text[..index] + newText + text[(index + oldText.Length)..];
    }
}
