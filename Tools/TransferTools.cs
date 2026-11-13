using System.Text;
using System.Text.Json;
using RemoteFs.Cache;
using RemoteFs.Common;
using RemoteFs.Mcp;
using RemoteFs.Services;
using RemoteFs.Ssh;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace RemoteFs.Tools;

/// <summary>
/// 文件传输工具：本地与远程之间的上传下载。
/// </summary>
public sealed class TransferTools
{
    private readonly RemoteFsService _service;

    public TransferTools(RemoteFsService service)
    {
        _service = service;
    }

    /// <summary>
    /// 构建传输工具
    /// </summary>
    public List<ToolDefinition> Build()
    {
        return new List<ToolDefinition>
        {
            BuildDownload(),
            BuildUpload(),
            BuildDownloadDirectory()
        };
    }

    /// <summary>
    /// 下载远程文件到本地
    /// </summary>
    private ToolDefinition BuildDownload()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("remotePath", SchemaBuilder.String("远程文件绝对路径")),
                ("localPath", SchemaBuilder.String("本地保存路径，目录则以原文件名保存到该目录")),
                ("overwrite", SchemaBuilder.Boolean("本地文件已存在时是否覆盖", false))
            },
            new[] { "remotePath", "localPath" });

        return new ToolDefinition
        {
            Name = "remote_download",
            Description = "把远程文件下载到本地。适合获取二进制文件、大文件或日志备份。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var remotePath = context.RequireString("remotePath");
                var localPath = context.RequireString("localPath");
                var overwrite = context.GetBool("overwrite");

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, remotePath);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, remotePath);

                if (!sftp.Exists(remotePath))
                {
                    return ToolResult.Error($"远程文件 {remotePath} 不存在");
                }

                var targetPath = ResolveLocalPath(localPath, remotePath);

                // 1.2 本机侧也要有边界，否则可以被引导着覆盖任意本地文件
                LocalPathGuard.EnsureAllowed(_service.Config, targetPath);

                if (File.Exists(targetPath) && !overwrite)
                {
                    return ToolResult.Error(
                        $"本地文件 {targetPath} 已存在。请指定其他路径，或设置 overwrite=true。");
                }

                var parent = Path.GetDirectoryName(Path.GetFullPath(targetPath));
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // 先下到缓存临时文件，成功后再落到目标位置，中断不会在目标处留半个文件
                var temporaryPath = _service.Cache.CreateTempFilePath();
                try
                {
                    using (var output = File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        sftp.DownloadFile(remotePath, output, null);
                    }

                    Cache.CacheStore.MoveIntoPlace(temporaryPath, targetPath);
                }
                catch
                {
                    Cache.CacheStore.TryDelete(temporaryPath);
                    throw;
                }

                var size = new FileInfo(targetPath).Length;
                AuditLogger.Info($"下载文件 [{profile.Name}] {remotePath} -> {targetPath} ({size} 字节)");

                return ToolResult.Ok(
                    $"已下载 {remotePath} 到本地 {targetPath}（{RemoteFsService.FormatSize(size)}）");
            }
        };
    }

    /// <summary>
    /// 上传本地文件到远程
    /// </summary>
    private ToolDefinition BuildUpload()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("localPath", SchemaBuilder.String("本地文件路径")),
                ("remotePath", SchemaBuilder.String("远程目标路径；以 / 结尾或指向已存在目录时，按目录处理并保留原文件名")),
                ("overwrite", SchemaBuilder.Boolean("远程文件已存在时是否覆盖", false))
            },
            new[] { "localPath", "remotePath" });

        return new ToolDefinition
        {
            Name = "remote_upload",
            Description = "把本地文件上传到远程主机。可用于部署配置文件、推送脚本。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var localPath = context.RequireString("localPath");
                var remotePath = context.RequireString("remotePath");
                var overwrite = context.GetBool("overwrite");

                var fullLocalPath = Path.GetFullPath(localPath);

                // 1.1 本机侧先校验，避免被引导着把私钥之类的东西传上去
                LocalPathGuard.EnsureAllowed(_service.Config, fullLocalPath);

                if (!File.Exists(fullLocalPath))
                {
                    return ToolResult.Error($"本地文件 {fullLocalPath} 不存在");
                }

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, remotePath);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, remotePath);

                // 1.2 判断目标是否按目录处理：显式以 / 结尾，或已经是目录
                var treatAsDirectory = remotePath.EndsWith('/');
                if (sftp.Exists(remotePath))
                {
                    treatAsDirectory = sftp.GetAttributes(remotePath).IsDirectory;
                }
                else if (treatAsDirectory)
                {
                    RemoteFileWriter.EnsureDirectory(sftp, remotePath);
                }

                // 2.1 目录语义下保留本地文件名
                var targetPath = treatAsDirectory
                    ? PathGuard.Combine(remotePath, Path.GetFileName(fullLocalPath))
                    : remotePath;

                _service.EnsurePathAllowed(profile, targetPath);
                // 目标可能是已存在的软链接，上传会写穿到链接目标，必须拦下
                _service.EnsureNotSymbolicLink(profile, sftp, targetPath);

                if (sftp.Exists(targetPath) && !overwrite)
                {
                    return ToolResult.Error(
                        $"远程文件 {targetPath} 已存在。请指定其他路径，或设置 overwrite=true。");
                }

                RemoteFileWriter.EnsureDirectory(sftp, RemoteFileWriter.GetParentDirectory(targetPath));

                using (var input = File.OpenRead(fullLocalPath))
                {
                    sftp.UploadFile(input, targetPath, true, null);
                }

                var size = sftp.GetAttributes(targetPath).Size;
                AuditLogger.Info($"上传文件 [{profile.Name}] {fullLocalPath} -> {targetPath} ({size} 字节)");

                return ToolResult.Ok(
                    $"已上传 {fullLocalPath} 到远程 {targetPath}（{RemoteFsService.FormatSize(size)}）");
            }
        };
    }

    /// <summary>
    /// 下载远程目录
    /// </summary>
    private ToolDefinition BuildDownloadDirectory()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("remotePath", SchemaBuilder.String("远程目录绝对路径")),
                ("localPath", SchemaBuilder.String("本地保存目录")),
                ("maxFiles", SchemaBuilder.Integer("最大下载文件数，默认 500", 1, 5000))
            },
            new[] { "remotePath", "localPath" });

        return new ToolDefinition
        {
            Name = "remote_download_dir",
            Description = "递归下载远程目录到本地目录。用于备份配置文件、获取整份站点配置。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Modifying,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var remotePath = context.RequireString("remotePath");
                var localPath = context.RequireString("localPath");
                var maxFiles = context.GetInt("maxFiles") ?? 500;

                // 1.1 白名单先于连接校验，越界路径不触发任何远程调用
                _service.EnsurePathAllowed(profile, remotePath);

                using var lease = _service.OpenSftp(profile);
                var sftp = lease.Client;
                _service.EnsureNotSymbolicLink(profile, sftp, remotePath);

                if (!sftp.Exists(remotePath))
                {
                    return ToolResult.Error($"远程目录 {remotePath} 不存在");
                }

                var fullLocalPath = Path.GetFullPath(localPath);

                // 1.2 本机侧先校验，越界路径不落任何文件
                LocalPathGuard.EnsureAllowed(_service.Config, fullLocalPath);

                Directory.CreateDirectory(fullLocalPath);

                var fileCount = 0;
                var totalSize = 0L;
                var skippedCount = 0;
                var reachedLimit = false;

                foreach (var file in EnumerateRemoteFiles(sftp, remotePath))
                {
                    // 1.2 达到上限直接停止遍历，不再空跑整棵目录树
                    if (fileCount >= maxFiles)
                    {
                        reachedLimit = true;
                        break;
                    }

                    var relativePath = file.FullName[remotePath.Length..].TrimStart('/');
                    var targetFile = Path.Combine(
                        fullLocalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var targetDirectory = Path.GetDirectoryName(targetFile);
                    if (!string.IsNullOrEmpty(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    try
                    {
                        using var output = File.Open(targetFile, FileMode.Create, FileAccess.Write, FileShare.None);
                        sftp.DownloadFile(file.FullName, output, null);
                        fileCount++;
                        totalSize += file.Length;
                    }
                    catch (Exception exception)
                    {
                        skippedCount++;
                        AuditLogger.Warn($"下载跳过 {file.FullName}: {exception.Message}");
                    }
                }

                AuditLogger.Info(
                    $"下载目录 [{profile.Name}] {remotePath} -> {fullLocalPath} ({fileCount} 个文件)");

                var message = $"已下载 {remotePath} 到本地 {fullLocalPath}，"
                    + $"共 {fileCount} 个文件（{RemoteFsService.FormatSize(totalSize)}）";
                if (reachedLimit)
                {
                    message += $"，已达到 maxFiles 上限 {maxFiles}，剩余文件未下载";
                }
                if (skippedCount > 0)
                {
                    message += $"，跳过 {skippedCount} 个读取失败的文件";
                }
                return ToolResult.Ok(message);
            }
        };
    }

    /// <summary>
    /// 解析本地保存路径：若为目录则拼接远程文件名
    /// </summary>
    private static string ResolveLocalPath(string localPath, string remotePath)
    {
        var fullPath = Path.GetFullPath(localPath);
        if (Directory.Exists(fullPath))
        {
            return Path.Combine(fullPath, Path.GetFileName(remotePath));
        }
        return fullPath;
    }

    /// <summary>
    /// 递归枚举远程普通文件，跳过软链接
    /// </summary>
    private static IEnumerable<ISftpFile> EnumerateRemoteFiles(SftpClient sftp, string directory)
    {
        List<ISftpFile> entries;
        try
        {
            entries = sftp.ListDirectory(directory).ToList();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取远程目录 {directory} 失败: {exception.Message}");
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            // 软链接不跟进，避免把白名单外或成环的内容一并拉下来
            if (entry.IsSymbolicLink)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                foreach (var nested in EnumerateRemoteFiles(sftp, entry.FullName))
                {
                    yield return nested;
                }
            }
            else if (entry.IsRegularFile)
            {
                yield return entry;
            }
        }
    }
}