using System.Text;
using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;
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
    /// <summary>递归复制与删除的深度上限，防止软链接或超深目录导致栈溢出</summary>
    private const int MaxRecursionDepth = 64;

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
            BuildPatch(),
            BuildMakeDirectory(),
            BuildRemove(),
            BuildMove(),
            BuildCopy(),
            BuildChmod(),
            BuildCreateLink()
        };
    }

    /// <summary>
    /// 按行号区间替换文件内容。内容不进上下文，适合大文件
    /// </summary>
    private ToolDefinition BuildPatch()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("path", SchemaBuilder.String("远程文件绝对路径")),
                ("startLine", SchemaBuilder.Integer("要替换的起始行号，从 1 开始", 1)),
                ("endLine", SchemaBuilder.Integer(
                    "要替换的结束行号（含该行），省略时只替换 startLine 这一行", 1)),
                ("content", SchemaBuilder.String(
                    "替换后的行内容。多行用换行分隔；空字符串表示删除这些行")),
                ("encoding", SchemaBuilder.String(
                    "替换内容使用的文本编码。缺省时按主机配置→全局配置取值（默认 utf-8）。"
                    + "只影响新写入的行，文件原有部分的字节原样保留"))
            },
            new[] { "path", "startLine", "content" });

        return new ToolDefinition
        {
            Name = "remote_patch",
            Description = "按行号区间替换远程文件的内容，只把新内容发上去，文件其余部分原样保留。"
                + "适合改大文件的某几行：内容不需要整体读进对话，内存占用与文件大小无关。"
                + "行号从 1 开始，endLine 含该行，content 为空表示删除这些行。"
                + "写入走临时文件 + rename，并保留原文件的权限与属主。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var startLine = context.GetInt("startLine") ?? 0;
                var endLine = context.GetInt("endLine") ?? startLine;
                var content = context.RequireText("content");

                // 1.1 行号必须先校验，越界的话后面会白跑一趟传输
                if (startLine < 1)
                {
                    return ToolResult.Error("startLine 必须从 1 开始");
                }
                if (endLine < startLine)
                {
                    return ToolResult.Error($"endLine({endLine}) 不能小于 startLine({startLine})");
                }

                // 1.2 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, path);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, path);

                if (!sftp.Exists(path))
                {
                    return ToolResult.Error($"文件 {path} 不存在");
                }

                var encodingName = _service.ResolveEncodingName(profile, context.GetString("encoding"));
                var encodingError = RemoteFsService.ValidateEncodingName(encodingName);
                if (encodingError is not null)
                {
                    return ToolResult.Error(encodingError);
                }

                byte[] payload;
                try
                {
                    payload = TextEncodingResolver.ResolveForWrite(encodingName).GetBytes(content);
                }
                catch (EncoderFallbackException)
                {
                    return ToolResult.Error(
                        $"无法用 {encodingName} 编码替换内容：文本中包含该编码无法表示的字符。"
                        + "请改用 encoding=gb18030 或 encoding=utf-8。");
                }

                // 2.1 记下原属性：服务端拼接用 mv 覆盖，文件会换成新 inode
                var originalAttributes = RemoteFileAttributes.Capture(sftp, path);

                // 2.2 替换内容单独传成一个临时文件，文件本体不出服务器
                var directory = RemoteFileWriter.GetParentDirectory(path);
                var fileName = Path.GetFileName(path);
                var payloadPath = PathGuard.Combine(directory, $".{fileName}.patch.{Guid.NewGuid():N}");
                var temporaryPath = PathGuard.Combine(directory, $".{fileName}.tmp.{Guid.NewGuid():N}");
                var writtenLines = CountLines(payload);

                try
                {
                    var payloadBytes = EnsureLineEnding(payload);
                    using (var payloadStream = sftp.Create(payloadPath))
                    {
                        payloadStream.Write(payloadBytes, 0, payloadBytes.Length);
                    }
                    writtenLines = CountLines(payloadBytes);

                    var command = RemoteFilePatcher.BuildSpliceCommand(
                        path, payloadPath, temporaryPath, startLine, endLine);
                    var output = _service.ExecuteCommand(profile, command, 120_000);

                    // 2.3 行号越界要单独报，别让用户以为是替换失败
                    if (output.ExitCode == RemoteFilePatcher.ExitLineOutOfRange)
                    {
                        var total = output.StandardOutput.Replace("OUT_OF_RANGE:", string.Empty).Trim();
                        var range = startLine == endLine
                            ? $"第 {startLine} 行"
                            : $"第 {startLine}-{endLine} 行";
                        return ToolResult.Error(
                            $"文件 {path} 只有 {total} 行，没有{range}。"
                            + "请先用 remote_read_file 确认行号。");
                    }
                    if (output.ExitCode != 0)
                    {
                        return ToolResult.Error(
                            $"按行替换失败（退出码 {output.ExitCode}）："
                            + $"{output.StandardError.Trim()}{output.StandardOutput.Trim()}");
                    }

                    _service.RestoreFileAttributes(profile, sftp, path, originalAttributes);
                }
                finally
                {
                    // 临时文件无论成败都要清掉
                    RemoteFileWriter.TryDelete(sftp, payloadPath);
                    RemoteFileWriter.TryDelete(sftp, temporaryPath);
                }

                AuditLogger.Info(
                    $"按行替换 [{profile.Name}] {path} 第 {startLine}-{endLine} 行 -> {writtenLines} 行");

                var summary = content.Length == 0
                    ? $"已删除 {path} 的第 {startLine}-{endLine} 行"
                    : $"已将 {path} 的第 {startLine}-{endLine} 行替换为 {writtenLines} 行";
                return ToolResult.Ok($"{summary}（主机 {profile.Name}）");
            }
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
                ("content", SchemaBuilder.String("要写入的文本内容，可为空字符串表示清空文件")),
                ("append", SchemaBuilder.Boolean("是否追加到文件末尾，默认覆盖", false)),
                ("createParents", SchemaBuilder.Boolean("父目录不存在时是否自动创建", true)),
                ("permissions", SchemaBuilder.String("文件权限位，如 644，默认保留原有权限")),
                ("encoding", SchemaBuilder.String(
                    "写入时使用的文本编码。缺省时按主机配置→全局配置取值（默认 utf-8，不写 BOM）。"
                    + "可选值：utf-8、utf-8-bom、utf-16le、utf-16be、utf-32le、utf-32be、"
                    + "gbk、gb2312、gb18030、big5、shift_jis、euc-jp、euc-kr、latin1。"
                    + "写 GBK/GB18030 文件必须显式指定 encoding=gb18030；追加写入不会写 BOM"))
            },
            new[] { "path", "content" });

        return new ToolDefinition
        {
            Name = "remote_write_file",
            Description = "将文本内容写入远程文件。默认覆盖整个文件，append=true 时追加到末尾。"
                + "内容原样写入，不做 shell 转义，适合写配置文件。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                // 空内容是合法写入意图（例如清空文件），用 RequireText 区分「没传」和「传了空串」
                var content = context.RequireText("content");
                var append = context.GetBool("append");
                var createParents = context.GetBool("createParents", true);
                var permissionsText = context.GetString("permissions");

                // 1.0 编码先校验：写入用的编码对未知名是静默回退 UTF-8 的，不校验会写错还不报错
                var encodingName = _service.ResolveEncodingName(profile, context.GetString("encoding"));
                var encodingError = RemoteFsService.ValidateEncodingName(encodingName);
                if (encodingError is not null)
                {
                    return ToolResult.Error(encodingError);
                }

                // 权限值非法必须在写入之前拦下，否则文件已改却返回失败
                short permissions = 0;
                if (!string.IsNullOrWhiteSpace(permissionsText)
                    && !TryParseOctalPermissions(permissionsText, out permissions))
                {
                    return ToolResult.Error($"权限值 '{permissionsText}' 不是合法的八进制权限，示例：644、755");
                }

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, path);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, path);

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

                // 1.2 覆盖写会把文件换成新 inode，先记下原有权限与属主
                var originalAttributes = existed && !append
                    ? RemoteFileAttributes.Capture(sftp, path)
                    : null;

                // 编码器是严格模式：目标编码表示不了的字符会抛异常，必须转成能自愈的提示
                try
                {
                    RemoteFileWriter.WriteText(
                        sftp, path, content, append, TextEncodingResolver.ResolveForWrite(encodingName));
                }
                catch (EncoderFallbackException)
                {
                    return ToolResult.Error(
                        $"无法用 {encodingName} 编码写入文件 {path}：文本中包含该编码无法表示的字符。"
                        + "请改用 encoding=gb18030 或 encoding=utf-8。");
                }

                // 1.3 显式指定权限就按指定值设，否则覆盖写要补回原来的属性。
                // SSH.NET 的 ChangePermissions 写入的模式位是错的，一律走服务端 chmod
                if (!string.IsNullOrWhiteSpace(permissionsText))
                {
                    var octal = Convert.ToString(permissions, 8).PadLeft(4, '0');
                    RunCommandOrThrow(profile, $"chmod {octal} -- {ShellQuoting.Quote(path)}");
                }
                else if (originalAttributes is not null)
                {
                    _service.RestoreFileAttributes(profile, sftp, path, originalAttributes);
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
                ("newText", SchemaBuilder.String("替换后的新文本，可为空字符串表示删除该段")),
                ("replaceAll", SchemaBuilder.Boolean("是否替换所有匹配项，默认只替换第一处", false)),
                ("encoding", SchemaBuilder.String(
                    "编辑时使用的文本编码。缺省时保持原文件的编码与 BOM 不变，"
                    + "但文件不是 UTF-8 时必须显式指定（如 encoding=gb18030），否则会解码失败。"
                    + "可选值：utf-8、utf-8-bom、gbk、gb18030、utf-16le、utf-16be、big5、shift_jis、latin1 等"))
            },
            new[] { "path", "oldText", "newText" });

        return new ToolDefinition
        {
            Name = "remote_edit_file",
            Description = "在远程文件中替换指定文本。比整体重写文件更安全，不会误删未读到的内容。"
                + "oldText 需精确匹配，匹配不到会报错并返回文件内容片段供确认。"
                + "文件超过单次读取上限时会拒绝编辑，避免只写回读到的那一段。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var oldText = context.RequireString("oldText");
                var newText = context.RequireText("newText");
                var replaceAll = context.GetBool("replaceAll");

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, path);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, path);

                // 1.2 记下原来的权限与属主：原子写是临时文件 + rename，不补就会被临时文件的属性顶掉
                var originalAttributes = RemoteFileAttributes.Capture(sftp, path);

                var (content, totalLength, truncated) = RemoteFileReader.Read(
                    sftp, path, 0, -1, _service.Config.MaxReadBytes, rejectWhenExceedsMaxBytes: true);

                // 1.2 只读到了文件的一部分，整体写回会静默截断
                if (truncated)
                {
                    return ToolResult.Error(
                        $"文件 {path} 大小 {totalLength} 字节，超过单次读取上限 {_service.Config.MaxReadBytes} 字节。"
                        + "此时整体写回会丢失未读取的内容，已拒绝编辑。"
                        + "请改用 remote_exec 配合 sed -i，或 remote_download → 本地修改 → remote_upload。");
                }

                var encodingName = _service.ResolveEncodingName(profile, context.GetString("encoding"));
                var encodingError = RemoteFsService.ValidateEncodingName(encodingName);
                if (encodingError is not null)
                {
                    return ToolResult.Error(encodingError);
                }

                var decoded = RemoteFileReader.DecodeText(content, encodingName, truncated: false);
                if (!decoded.Success)
                {
                    // 区分「真的是二进制」和「只是编码不对」，后者要给出可自愈的提示
                    return decoded.IsBinary
                        ? ToolResult.Error($"文件 {path} 是二进制内容，无法做文本替换")
                        : ToolResult.Error(
                            $"文件 {path} 无法按 {decoded.EncodingName} 解码：{decoded.Error}{Environment.NewLine}"
                            + "该文件很可能使用 GBK/GB18030 等中文编码，请改用 encoding=gb18030 重试。");
                }

                var text = decoded.Text ?? string.Empty;

                var occurrences = CountOccurrences(text, oldText);
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

                // 1.3 原子写入：先写临时文件再重命名，避免写到一半失败损坏原文件。
                // 写回时保持原文件的编码与 BOM，编辑 UTF-8-BOM 文件不能顺手把 BOM 抹掉
                var (rewriteEncoding, writeBom) =
                    TextEncodingResolver.ResolveForRewrite(decoded.EncodingName, decoded.HasBom);
                try
                {
                    RemoteFileWriter.WriteTextAtomic(sftp, path, updated, rewriteEncoding, writeBom);
                }
                catch (EncoderFallbackException)
                {
                    return ToolResult.Error(
                        $"无法用 {decoded.EncodingName} 编码写回文件 {path}：新文本中包含该编码无法表示的字符。"
                        + "请改用 encoding=gb18030 或 encoding=utf-8。");
                }

                // 1.4 rename 之后补回原来的权限与属主
                _service.RestoreFileAttributes(profile, sftp, path, originalAttributes);

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
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var createParents = context.GetBool("createParents", true);

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, path);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, path);

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
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var recursive = context.GetBool("recursive");

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, path);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;

                if (!sftp.Exists(path))
                {
                    return ToolResult.Ok($"路径 {path} 不存在，无需删除");
                }

                // 1.2 软链接必须走服务端 rm：SftpClient.DeleteFile 会解析链接并删掉目标文件
                var isSymbolicLink = RemotePathInspector.IsSymbolicLink(sftp, path);
                var attributes = sftp.GetAttributes(path);
                var isDirectory = attributes.IsDirectory && !isSymbolicLink;

                if (isDirectory)
                {
                    if (!recursive)
                    {
                        var entries = sftp.ListDirectory(path)
                            .Where(entry => entry.Name is not ("." or ".."))
                            .ToList();
                        if (entries.Count > 0)
                        {
                            return ToolResult.Error(
                                $"目录 {path} 非空（{entries.Count} 项）。请设置 recursive=true 递归删除。");
                        }
                    }
                    // rm -rf 对符号链接只删链接本身，不会跟进目标
                    RunCommandOrThrow(profile, $"rm -rf -- {ShellQuoting.Quote(path)}");
                }
                else if (isSymbolicLink)
                {
                    RunCommandOrThrow(profile, $"rm -f -- {ShellQuoting.Quote(path)}");
                }
                else
                {
                    sftp.DeleteFile(path);
                }

                AuditLogger.Info(
                    $"删除路径 [{profile.Name}] {path} (recursive={recursive}, link={isSymbolicLink})");
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
                ("destination", SchemaBuilder.String("目标路径绝对路径")),
                ("overwrite", SchemaBuilder.Boolean("目标已存在时是否覆盖，默认不覆盖", false))
            },
            new[] { "source", "destination" });

        return new ToolDefinition
        {
            Name = "remote_move",
            Description = "移动或重命名远程文件/目录。目标已存在时默认失败，需显式设置 overwrite=true 才覆盖。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var source = context.RequireString("source");
                var destination = context.RequireString("destination");
                var overwrite = context.GetBool("overwrite");

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, source);
                _service.EnsurePathAllowed(profile, destination);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, source);
                _service.EnsureNotSymbolicLink(profile, sftp, destination);

                if (!sftp.Exists(source))
                {
                    return ToolResult.Error($"源路径 {source} 不存在");
                }

                // 1.2 默认不覆盖，避免 AI 误伤已有文件
                if (sftp.Exists(destination) && !overwrite)
                {
                    return ToolResult.Error(
                        $"目标路径 {destination} 已存在。如需覆盖请设置 overwrite=true。");
                }

                sftp.RenameFile(source, destination, overwrite);
                AuditLogger.Info($"移动路径 [{profile.Name}] {source} -> {destination} (overwrite={overwrite})");
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
            Description = "在远程主机内部复制文件或目录，全程走 SFTP 读写，不经过本地磁盘。目录复制默认递归。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var source = context.RequireString("source");
                var destination = context.RequireString("destination");
                var recursive = context.GetBool("recursive", true);

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, source);
                _service.EnsurePathAllowed(profile, destination);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, source);
                _service.EnsureNotSymbolicLink(profile, sftp, destination);

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
                    CopyDirectory(sftp, source, destination, 0);
                }
                else
                {
                    RemoteFileWriter.EnsureDirectory(
                        sftp, RemoteFileWriter.GetParentDirectory(destination));
                    CopyFile(sftp, source, destination);
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
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var path = context.RequireString("path");
                var permissionsText = context.RequireString("permissions");

                if (!TryParseOctalPermissions(permissionsText, out var permissions))
                {
                    return ToolResult.Error($"权限值 '{permissionsText}' 不是合法的八进制权限，示例：644、755");
                }

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, path);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, path);

                if (!sftp.Exists(path))
                {
                    return ToolResult.Error($"路径 {path} 不存在");
                }

                // SFTP 的权限写入在部分服务端实现上会落成错误的模式（实测请求 600 得到 404），
                // 改用服务端自带的 chmod，结果与手工执行完全一致
                var octalDisplay = Convert.ToString(permissions, 8).PadLeft(3, '0');
                var output = _service.ExecuteCommand(
                    profile, $"chmod {octalDisplay} {ShellQuoting.Quote(path)}", null);
                if (output.ExitCode != 0)
                {
                    return ToolResult.Error(
                        $"修改权限失败（退出码 {output.ExitCode}）：{output.StandardError.Trim()}");
                }

                AuditLogger.Info($"修改权限 [{profile.Name}] {path} -> {octalDisplay}");
                return ToolResult.Ok($"已将 {path}（主机 {profile.Name}）权限修改为 {octalDisplay}");
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
            Description = "在远程主机上创建符号链接（软链接）。target 与 linkPath 都必须落在白名单范围内。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var target = context.RequireString("target");
                var linkPath = context.RequireString("linkPath");

                // 1.1 链接目标同样要校验，否则可以绕过白名单把外部路径挂进来
                _service.EnsurePathAllowed(profile, target);
                _service.EnsurePathAllowed(profile, linkPath);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, linkPath);

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
    /// 在远程主机内复制单个文件，读写都走同一个 SFTP 会话
    /// </summary>
    private static void CopyFile(SftpClient sftp, string source, string destination)
    {
        using var input = sftp.OpenRead(source);
        using var output = sftp.OpenWrite(destination);
        input.CopyTo(output, 64 * 1024);
    }

    /// <summary>
    /// 递归复制目录，全程远程读写
    /// </summary>
    private static void CopyDirectory(SftpClient sftp, string source, string destination, int depth)
    {
        if (depth > MaxRecursionDepth)
        {
            throw new InvalidOperationException(
                $"复制目录层级超过 {MaxRecursionDepth}，已中止，请检查是否存在异常的深目录结构");
        }

        RemoteFileWriter.EnsureDirectory(sftp, destination);

        // 同样先取快照，避免流式枚举过程中目标目录的写入干扰
        foreach (var entry in sftp.ListDirectory(source).ToList())
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var target = PathGuard.Combine(destination, entry.Name);

            // 软链接只复制链接本身，不跟进目标，避免复制到白名单外或成环
            if (entry.IsSymbolicLink)
            {
                sftp.SymbolicLink(entry.FullName, target);
                continue;
            }

            if (entry.IsDirectory)
            {
                CopyDirectory(sftp, entry.FullName, target, depth + 1);
            }
            else if (entry.IsRegularFile)
            {
                CopyFile(sftp, entry.FullName, target);
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
    /// 执行服务端命令，非零退出码直接抛出，避免「命令失败但工具报成功」
    /// </summary>
    private void RunCommandOrThrow(HostProfile profile, string command)
    {
        var output = _service.ExecuteCommand(profile, command, null);
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"执行 {command} 失败（退出码 {output.ExitCode}）：{output.StandardError.Trim()}");
        }
    }

    /// <summary>
    /// 保证替换内容以换行结尾，避免和下面一行粘在一起
    /// </summary>
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

    /// <summary>
    /// 统计字节里的行数
    /// </summary>
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
        // 末尾没有换行符时，最后一段也算一行
        return content[^1] == (byte)'\n' ? count : count + 1;
    }

    /// <summary>
    /// 统计文本出现次数
    /// </summary>
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