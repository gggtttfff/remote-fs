using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Cache;

/// <summary>
/// 缓存条目：远程目录枚举结果的精简表示
/// </summary>
public sealed class CachedFileEntry
{
    public string Name { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public long Length { get; set; }

    public bool IsDirectory { get; set; }

    public bool IsSymbolicLink { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }
}

/// <summary>
/// 缓存文件
/// </summary>
public sealed class CachedListing
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<CachedFileEntry> Files { get; set; } = new();
}

/// <summary>
/// 缓存存储：程序目录下 cache/
///   tmp/       下载临时文件，落位后删除
///   listings/  远程目录枚举结果，按主机隔离
/// </summary>
public sealed class CacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _syncRoot = new();
    private readonly AppConfig _config;

    public CacheStore(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 缓存根目录：程序目录下 cache/
    /// </summary>
    public string RootDirectory => ConfigStore.CacheDirectory;

    /// <summary>
    /// 确保缓存目录存在。任何子命令启动时都会调用，不只 serve 模式
    /// </summary>
    public static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(ConfigStore.CacheDirectory, "tmp"));
            Directory.CreateDirectory(Path.Combine(ConfigStore.CacheDirectory, "listings"));
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"创建缓存目录失败: {exception.Message}");
        }
    }

    /// <summary>下载临时文件目录</summary>
    public string TempDirectory => Path.Combine(RootDirectory, "tmp");

    /// <summary>目录枚举缓存目录</summary>
    public string ListingDirectory => Path.Combine(RootDirectory, "listings");

    /// <summary>
    /// 初始化缓存目录，并清理过期的临时文件
    /// </summary>
    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(TempDirectory);
            Directory.CreateDirectory(ListingDirectory);
            CleanupExpiredTempFiles();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"初始化缓存目录失败: {exception.Message}");
        }
    }

    /// <summary>
    /// 清理超过一小时的下载临时文件
    /// </summary>
    public int CleanupExpiredTempFiles()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(TempDirectory))
            {
                return 0;
            }

            var expiredBefore = DateTime.UtcNow.AddHours(-1);
            foreach (var path in Directory.GetFiles(TempDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < expiredBefore)
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (Exception)
                {
                    // 被占用的临时文件跳过
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程
        }

        return removed;
    }

    /// <summary>
    /// 申请一个下载临时文件路径
    /// </summary>
    public string CreateTempFilePath()
    {
        try
        {
            Directory.CreateDirectory(TempDirectory);
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"创建缓存临时目录失败: {exception.Message}");
        }

        return Path.Combine(TempDirectory, $"{Guid.NewGuid():N}.part");
    }

    /// <summary>
    /// 把临时文件移动到最终位置。跨盘时退化为复制后删除。
    /// </summary>
    public static void MoveIntoPlace(string temporaryPath, string targetPath)
    {
        try
        {
            File.Move(temporaryPath, targetPath, overwrite: true);
            return;
        }
        catch (IOException)
        {
            // 跨卷移动失败，退化成复制
        }

        File.Copy(temporaryPath, targetPath, overwrite: true);
        TryDelete(temporaryPath);
    }

    /// <summary>
    /// 删除文件，失败不抛异常
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>
    /// 读取目录列举缓存，超过有效期视为未命中
    /// </summary>
    public List<CachedFileEntry>? GetListing(string hostName, string directory)
    {
        if (!_config.CacheEnabled)
        {
            return null;
        }

        var path = BuildListingPath(hostName, directory);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var cached = JsonSerializer.Deserialize<CachedListing>(json, JsonOptions);
            if (cached is null)
            {
                return null;
            }

            var age = DateTime.UtcNow - cached.CreatedUtc;
            if (age > TimeSpan.FromSeconds(_config.CacheTtlSeconds))
            {
                return null;
            }

            return cached.Files;
        }
        catch (Exception)
        {
            // 缓存损坏就当没命中
            return null;
        }
    }

    /// <summary>
    /// 写入目录列举缓存。缓存的是单个目录的完整列举结果，可以独立复用
    /// </summary>
    public void SaveListing(string hostName, string directory, List<CachedFileEntry> files)
    {
        if (!_config.CacheEnabled)
        {
            return;
        }

        var path = BuildListingPath(hostName, directory);
        try
        {
            Directory.CreateDirectory(ListingDirectory);
            var cached = new CachedListing { CreatedUtc = DateTime.UtcNow, Files = files };
            File.WriteAllText(path, JsonSerializer.Serialize(cached, JsonOptions));
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"写入目录缓存失败: {exception.Message}");
        }
    }

    /// <summary>
    /// 让某台主机的缓存全部失效。写操作后调用，避免看到过期的目录结构。
    /// </summary>
    public int InvalidateHost(string hostName)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(ListingDirectory))
            {
                return 0;
            }

            var prefix = SanitizeFileName(hostName) + "-";
            foreach (var path in Directory.GetFiles(ListingDirectory, prefix + "*.json"))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        return removed;
    }

    /// <summary>
    /// 清空全部缓存
    /// </summary>
    public int ClearAll()
    {
        var removed = 0;
        foreach (var directory in new[] { TempDirectory, ListingDirectory })
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var path in Directory.GetFiles(directory))
                {
                    TryDelete(path);
                    removed++;
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        return removed;
    }

    /// <summary>
    /// 缓存文件名：主机名 + 目录路径的哈希
    /// </summary>
    private string BuildListingPath(string hostName, string directory)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(directory))).ToLowerInvariant()[..16];
        return Path.Combine(ListingDirectory, $"{SanitizeFileName(hostName)}-{hash}.json");
    }

    /// <summary>
    /// 把主机名转成安全的文件名片段
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }
}