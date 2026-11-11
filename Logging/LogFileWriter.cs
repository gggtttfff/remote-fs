using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteFs.Logging;

/// <summary>
/// 日志文件写入器：JSONL 格式，按大小自动分割，按天数与数量自动清理。
/// 采用追加写并允许多进程同时打开，Windows 上清理被占用的文件会跳过而不是报错。
/// </summary>
public sealed class LogFileWriter : IDisposable
{
    private const string ActiveFileName = "remote-fs.log";
    private const string ArchivePrefix = "remote-fs-";
    private const string ArchiveSuffix = ".log";

    /// <summary>攒够多少条落一次盘，降低写盘频率</summary>
    private const int FlushEveryEntries = 20;

    private readonly object _syncRoot = new();
    private readonly LogSettings _settings;
    private StreamWriter? _writer;
    private long _currentFileSize;
    private int _pendingEntries;
    private bool _disabled;
    private string? _disabledReason;

    public LogFileWriter(LogSettings settings)
    {
        _settings = settings;
    }

    /// <summary>日志目录，默认取 exe 同级的 log 目录</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "log");

    /// <summary>日志是否因目录不可写而禁用</summary>
    public bool IsDisabled => _disabled;

    /// <summary>禁用原因</summary>
    public string? DisabledReason => _disabledReason;

    /// <summary>当前活动的日志文件路径</summary>
    public string ActiveFilePath => Path.Combine(Directory, ActiveFileName);

    /// <summary>日志目录</summary>
    public string Directory { get; private set; } = DefaultDirectory;

    /// <summary>
    /// 探测目录可写性。不可写时日志功能整体禁用，不做任何回退。
    /// </summary>
    public void Initialize(string? directory = null)
    {
        lock (_syncRoot)
        {
            Directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                RestrictPermissions(Directory);

                // 写一个探针文件，确认真的有写权限
                var probePath = Path.Combine(Directory, ".write-probe");
                File.WriteAllText(probePath, "probe");
                File.Delete(probePath);
            }
            catch (Exception exception)
            {
                Disable($"日志目录不可写: {Directory}（{exception.Message}）");
                return;
            }

            _disabled = false;
            _disabledReason = null;
        }
    }

    /// <summary>
    /// 写一条日志。失败只记 stderr，不影响主流程。
    /// </summary>
    public void Write(LogEntry entry)
    {
        if (_disabled)
        {
            return;
        }

        var line = entry.ToJsonLine();

        lock (_syncRoot)
        {
            try
            {
                EnsureWriter();
                if (_writer is null)
                {
                    return;
                }

                _writer.WriteLine(line);
                _currentFileSize += Encoding.UTF8.GetByteCount(line) + 1;
                _pendingEntries++;

                // 1. 攒够一批再落盘，减少磁盘写入次数
                if (_pendingEntries >= FlushEveryEntries)
                {
                    FlushCore();
                }

                // 2. 超过上限就分割
                if (_currentFileSize >= _settings.MaxFileSizeBytes)
                {
                    Rotate();
                }
            }
            catch (Exception exception)
            {
                Disable($"写入日志失败: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// 清理过期日志：按保留天数与最多文件数双重限制。
    /// 归档文件写完就不再被占用，可以安全删除。
    /// </summary>
    public int Cleanup()
    {
        if (_disabled || !System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            // 1. 活动文件正在被写，永远不删
            var activePath = Path.Combine(Directory, ActiveFileName);

            var archives = System.IO.Directory
                .GetFiles(Directory, $"{ArchivePrefix}*{ArchiveSuffix}")
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            var expiredBefore = DateTime.UtcNow.AddDays(-_settings.RetentionDays);
            var keep = new HashSet<string>(
                archives.Take(_settings.MaxFiles).Select(info => info.FullName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var info in archives)
            {
                var isExpired = info.LastWriteTimeUtc < expiredBefore;
                var isOverCount = !keep.Contains(info.FullName);
                if (!isExpired && !isOverCount)
                {
                    continue;
                }

                try
                {
                    info.Delete();
                    removed++;
                }
                catch (Exception)
                {
                    // 正在被写入的日志文件删不掉，留到下一轮
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
    /// 清空全部日志：删除所有日志文件并重建活动文件
    /// </summary>
    public bool ClearAll()
    {
        lock (_syncRoot)
        {
            try
            {
                FinishCurrentWriter();
                _writer?.Dispose();
                _writer = null;

                if (System.IO.Directory.Exists(Directory))
                {
                    foreach (var path in System.IO.Directory.GetFiles(Directory, "*.log"))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception)
                        {
                            // 被占用的文件跳过
                        }
                    }
                }

                _currentFileSize = 0;
                EnsureWriter();
                return true;
            }
            catch (Exception exception)
            {
                Disable($"清空日志失败: {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 列出全部日志文件，新的在前
    /// </summary>
    public static IReadOnlyList<FileInfo> ListLogFiles(string? directory = null)
    {
        var target = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        if (!System.IO.Directory.Exists(target))
        {
            return new List<FileInfo>();
        }

        try
        {
            return System.IO.Directory
                .GetFiles(target, "*.log")
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return new List<FileInfo>();
        }
    }

    /// <summary>
    /// 立即落盘，进程退出前必须调用，否则最后不足一批的日志会丢
    /// </summary>
    public void Flush()
    {
        lock (_syncRoot)
        {
            FlushCore();
        }
    }

    /// <summary>
    /// 落盘并把计数归零，调用方需持有 _syncRoot
    /// </summary>
    private void FlushCore()
    {
        try
        {
            _writer?.Flush();
            _pendingEntries = 0;
        }
        catch (Exception exception)
        {
            Disable($"日志落盘失败: {exception.Message}");
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        var path = Path.Combine(Directory, ActiveFileName);
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        _currentFileSize = stream.Length;
        _pendingEntries = 0;
    }

    /// <summary>
    /// 轮转前把当前文件收尾，保证最后一行完整
    /// </summary>
    private void FinishCurrentWriter()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception)
        {
            // 收尾失败不影响后续写入
        }
    }

    /// <summary>
    /// 分割：把活动文件改名为带时间戳的归档文件，然后重开新的活动文件
    /// </summary>
    private void Rotate()
    {
        try
        {
            FinishCurrentWriter();
            _writer?.Dispose();
            _writer = null;

            var activePath = Path.Combine(Directory, ActiveFileName);
            if (File.Exists(activePath))
            {
                var archivePath = Path.Combine(
                    Directory,
                    $"{ArchivePrefix}{DateTime.Now:yyyyMMdd-HHmmss}{ArchiveSuffix}");
                File.Move(activePath, archivePath);
            }

            _currentFileSize = 0;
            EnsureWriter();
            Cleanup();
        }
        catch (Exception exception)
        {
            Disable($"分割日志失败: {exception.Message}");
        }
    }

    /// <summary>
    /// 收紧目录权限，日志里可能有服务器上的敏感内容
    /// </summary>
    private static void RestrictPermissions(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 去掉继承的 Users/Everyone 权限，只留当前用户和 Administrators
                var security = new System.Security.AccessControl.DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (currentUser is not null)
                {
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        currentUser,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                }
                new System.IO.DirectoryInfo(directory).SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception)
        {
            // 权限收紧失败不阻断日志功能
        }
    }

    private void Disable(string reason)
    {
        _disabled = true;
        _disabledReason = reason;
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            try
            {
                // 退出前把攒着的日志落盘，否则最后不足一批的会丢
                FlushCore();
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }
            _writer = null;
        }
    }
}
