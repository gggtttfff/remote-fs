using System.Text;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace RemoteFs.Services;

/// <summary>
/// 远程任务管理器。
/// 每次命令执行都落成一个带文件的远程任务目录，输出可增量读取，终止按进程组下发。
/// 服务器上的任务目录是唯一事实来源，MCP 重启后仍能按目录接管遗留任务。
/// </summary>
public sealed class RemoteJobManager : IDisposable
{
    private const int StartConfirmationMilliseconds = 5_000;
    private const int LaunchConfirmationMilliseconds = 1_000;
    private const int ShortCommandTimeoutMilliseconds = 30_000;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly AppConfig _config;
    private readonly SftpConnectionPool _pool;
    private readonly SshConnectionPool _sshPool;
    private readonly Dictionary<string, HostEnvironment> _environments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastCleanupUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(HostProfile Profile, RemoteJobHandle Handle)> _startedJobs = new();
    private readonly object _syncRoot = new();
    private bool _disposed;

    public RemoteJobManager(
        AppConfig config, SftpConnectionPool pool, SshConnectionPool sshPool)
    {
        _config = config;
        _pool = pool;
        _sshPool = sshPool;
    }

    /// <summary>
    /// 提交任务并返回句柄，不等待命令结束
    /// </summary>
    public RemoteJobHandle StartJob(
        HostProfile profile, string command, int ttlSeconds, string outputEncoding)
    {
        using var lease = _pool.AcquireWithFallback(profile);
        var sftp = lease.Client;
        var environment = ResolveEnvironment(profile, sftp);

        var jobId = BuildJobId();
        var directory = environment.JobRoot + "/" + jobId;

        // 1.1 准备任务目录，写入命令与两个脚本。
        // cmd 会被远端 shell 当字节读，run.sh / watchdog.sh 里也带中文注释，
        // 三者一律 UTF-8，不跟随配置编码，否则命令本身会被写坏
        RemoteFileWriter.EnsureDirectory(sftp, directory);
        sftp.WriteAllText(directory + "/cmd", command, Utf8NoBom);
        sftp.WriteAllText(
            directory + "/run.sh",
            RemoteJobScripts.BuildRunScript(directory, environment.ShellPath, environment.UseSetsid),
            Utf8NoBom);
        sftp.WriteAllText(
            directory + "/watchdog.sh",
            RemoteJobScripts.BuildWatchdogScript(directory, ttlSeconds),
            Utf8NoBom);

        // 1.2 输出编码随任务留在服务器上：MCP 重启后接管遗留任务时仍知道该用什么解码
        sftp.WriteAllText(directory + "/encoding", outputEncoding, Utf8NoBom);

        // 1.2 启动；脚本自己脱离会话，SSH 通道关闭不会波及它
        var launch = RemoteJobScripts.BuildLaunchCommand(directory, environment.UseSetsid);
        ExecuteLaunchCommand(profile, launch);

        // 1.3 等 pid 文件出现，确认进程真的起来了，避免「提交成功但其实没跑」
        var processGroupId = WaitForProcessGroupId(profile, sftp, directory);
        if (processGroupId is null)
        {
            throw new InvalidOperationException(
                $"任务 {jobId} 已提交但 5 秒内没有写出 pid，可能远程缺少 sh 或任务目录不可写。"
                + $"任务目录 {directory}，可执行 remote_job_status 或直接查看该目录下的 err 文件。");
        }

        var handle = new RemoteJobHandle(
            profile.Name, jobId, directory, command, DateTime.UtcNow, outputEncoding);
        RememberStarted(profile, handle);

        AuditLogger.Info(
            $"任务已提交 [{profile.Name}] {jobId} pid={processGroupId} ttl={ttlSeconds}s: {command}");

        CleanupRetentionIfDue(profile, sftp);
        return handle;
    }

    /// <summary>
    /// 在服务器上等任务写出退出码，1 次往返。
    /// 命令写成 while 循环，文件一出现就立刻返回，不必等轮询粒度。
    /// </summary>
    private (bool Finished, int? ExitCode) WaitForExitFile(
        HostProfile profile, string exitPath, int timeoutMilliseconds)
    {
        var command = RemoteJobScripts.BuildWaitForExitCommand(exitPath);

        try
        {
            using var lease = _sshPool.Acquire(profile, out _);
            using var runner = lease.Client.CreateCommand(command);
            runner.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
            runner.Execute();

            var output = runner.Result?.Trim();
            return (true, int.TryParse(output, out var parsed) ? parsed : (int?)null);
        }
        catch (SshOperationTimeoutException)
        {
            // 命令还没结束就超时了，任务仍在远端跑
            return (false, null);
        }
        catch (Agent.SshUnavailableException)
        {
            // SSH 整个连不上，要让上层改走备用端口，不能在这里吞掉
            throw;
        }
        catch (Exception exception)
        {
            // 通道断了之类的情况，退回单次 SFTP 确认，至少能判断出任务到底结束没有
            AuditLogger.Warn($"等待任务结束失败，改用一次性确认: {exception.Message}");
            return ReadExitFileOnce(profile, exitPath);
        }
    }

    /// <summary>
    /// 一次性读退出码文件，判断任务是否已经结束
    /// </summary>
    private (bool Finished, int? ExitCode) ReadExitFileOnce(HostProfile profile, string exitPath)
    {
        try
        {
            using var lease = _pool.AcquireWithFallback(profile);
            var raw = TryReadText(lease.Client, exitPath)?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return (false, null);
            }

            return (true, int.TryParse(raw, out var parsed) ? parsed : (int?)null);
        }
        catch (Agent.SshUnavailableException)
        {
            // SSH 整个连不上，要让上层改走备用端口，不能在这里吞掉
            throw;
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取退出码失败: {exception.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// 前台等待：返回退出码与全部输出；超时则终止整个进程组，并保留已产生的输出
    /// </summary>
    public ForegroundJobResult WaitForCompletion(
        HostProfile profile, RemoteJobHandle handle, int timeoutMilliseconds)
    {
        var exitPath = handle.RemoteDirectory + "/exit";

        // 在服务器上等 exit 文件出现，1 次往返拿结果。
        // 原来每 250ms 轮询一次 SFTP，60 秒的命令要 240 次往返，
        // 而且返回时间被轮询粒度拖着走。
        var (finished, exitCode) = WaitForExitFile(profile, exitPath, timeoutMilliseconds);

        if (!finished)
        {
            Kill(profile, handle, force: false);
        }

        // 2.1 无论成功还是终止，都把手上的输出带回去
        string stdout;
        string stderr;
        bool truncated;
        using (var lease = _pool.AcquireWithFallback(profile))
        {
            var sftp = lease.Client;
            (stdout, var stdoutTruncated) = ReadTextOrEmpty(
                sftp, handle.RemoteDirectory + "/out", _config.MaxReadBytes, handle.OutputEncoding);
            (stderr, var stderrTruncated) = ReadTextOrEmpty(
                sftp, handle.RemoteDirectory + "/err", _config.MaxReadBytes, handle.OutputEncoding);
            truncated = stdoutTruncated || stderrTruncated;
        }

        if (!finished)
        {
            AuditLogger.Warn(
                $"命令超过 {timeoutMilliseconds} 毫秒已终止 [{profile.Name}] {handle.JobId}: {handle.Command}");
        }

        return new ForegroundJobResult(
            !finished,
            exitCode,
            stdout,
            stderr,
            truncated,
            handle);
    }

    /// <summary>
    /// 查询任务状态
    /// </summary>
    public RemoteJobInfo GetStatus(HostProfile profile, RemoteJobHandle handle)
    {
        using var lease = _pool.AcquireWithFallback(profile);
        return BuildStatus(lease.Client, handle.JobId, handle.RemoteDirectory);
    }

    /// <summary>
    /// 读取增量输出
    /// </summary>
    public (JobOutputChunk Stdout, JobOutputChunk Stderr, RemoteJobInfo Info) ReadOutput(
        HostProfile profile, RemoteJobHandle handle,
        long stdoutOffset, long stderrOffset, int maxBytes, string? encodingOverride = null)
    {
        using var lease = _pool.AcquireWithFallback(profile);
        var sftp = lease.Client;

        // 输出编码以任务目录里记录的为准，工具参数可以做单次覆盖
        var encodingName = string.IsNullOrWhiteSpace(encodingOverride)
            ? handle.OutputEncoding
            : TextEncodingResolver.Normalize(encodingOverride);

        var stdout = ReadChunk(sftp, handle.RemoteDirectory + "/out", stdoutOffset, maxBytes, encodingName);
        var stderr = ReadChunk(sftp, handle.RemoteDirectory + "/err", stderrOffset, maxBytes, encodingName);
        var info = BuildStatus(sftp, handle.JobId, handle.RemoteDirectory);
        return (stdout, stderr, info);
    }

    /// <summary>
    /// 列出远程任务目录下的任务，最新的排前面
    /// </summary>
    public List<RemoteJobInfo> List(HostProfile profile, int limit, bool includeFinished)
    {
        using var lease = _pool.AcquireWithFallback(profile);
        var sftp = lease.Client;
        var environment = ResolveEnvironment(profile, sftp);
        var jobs = new List<RemoteJobInfo>();

        if (!SafeExists(sftp, environment.JobRoot))
        {
            return jobs;
        }

        List<ISftpFile> directories;
        try
        {
            directories = sftp.ListDirectory(environment.JobRoot)
                .Where(entry => entry.IsDirectory && entry.Name.StartsWith("job-", StringComparison.Ordinal))
                .OrderByDescending(entry => entry.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取任务目录 {environment.JobRoot} 失败: {exception.Message}");
            return jobs;
        }

        foreach (var directory in directories)
        {
            if (jobs.Count >= limit)
            {
                break;
            }

            var info = BuildStatus(sftp, directory.Name, environment.JobRoot + "/" + directory.Name);
            if (!includeFinished && info.State != RemoteJobState.Running)
            {
                continue;
            }
            jobs.Add(info);
        }

        CleanupRetention(profile, sftp, directories);
        return jobs;
    }

    /// <summary>
    /// 按 jobId 找到任务句柄，找不到返回 null
    /// </summary>
    public RemoteJobHandle? FindJob(HostProfile profile, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        // 1.0 任务号来自模型入参，会被拼进远端路径。格式不对直接拒绝，
        // 否则 ../../ 能穿到任务目录之外，kill 会读到目录外的 pid 文件
        if (!IsWellFormedJobId(jobId))
        {
            AuditLogger.Warn($"拒绝格式非法的任务号: {jobId}");
            return null;
        }

        // 1.1 本进程提交过的直接命中，省掉一次目录扫描
        lock (_syncRoot)
        {
            var known = _startedJobs.FirstOrDefault(item =>
                string.Equals(item.Handle.JobId, jobId, StringComparison.Ordinal)
                && string.Equals(item.Profile.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (known.Handle is not null)
            {
                return known.Handle;
            }
        }

        // 1.2 没有记录就按服务器上的目录推断，兼容 MCP 重启后的遗留任务
        using var lease = _pool.AcquireWithFallback(profile);
        var sftp = lease.Client;
        var environment = ResolveEnvironment(profile, sftp);
        var directory = environment.JobRoot + "/" + jobId;
        if (!SafeExists(sftp, directory))
        {
            return null;
        }

        var command = TryReadText(sftp, directory + "/cmd") ?? string.Empty;

        // 接管遗留任务时把提交时记录的输出编码一并读回来，缺失则按 UTF-8 兜底
        var outputEncoding = TryReadText(sftp, directory + "/encoding")?.Trim();
        if (string.IsNullOrWhiteSpace(outputEncoding))
        {
            outputEncoding = TextEncodingResolver.Normalize(null);
        }

        return new RemoteJobHandle(
            profile.Name, jobId, directory, command, DateTime.UtcNow, outputEncoding);
    }

    /// <summary>
    /// 任务号形如 job-20261111-153012-a3f2。只允许字母数字和横线，
    /// 挡住 ../ 与分隔符被拼进远端路径
    /// </summary>
    private static bool IsWellFormedJobId(string jobId)
    {
        return jobId.StartsWith("job-", StringComparison.Ordinal)
            && jobId.Length <= 64
            && jobId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    /// <summary>
    /// 终止任务：先核对启动时间，避免 pid 复用误杀无关进程
    /// </summary>
    public (bool Success, string Message) Kill(HostProfile profile, RemoteJobHandle handle, bool force)
    {
        using var lease = _pool.AcquireWithFallback(profile);
        var sftp = lease.Client;
        var directory = handle.RemoteDirectory;

        var processGroupId = ReadProcessGroupId(sftp, directory);
        if (processGroupId is null)
        {
            return (false, $"任务 {handle.JobId} 没有 pid 记录，无法定位进程。");
        }

        // 1.1 pid 复用护栏：启动时间对不上说明这个 pid 已经属于别的进程
        var recordedStartTime = TryReadText(sftp, directory + "/starttime")?.Trim();
        var currentStartTime = ReadProcessStartTime(sftp, processGroupId.Value);
        if (!string.IsNullOrEmpty(recordedStartTime)
            && !string.IsNullOrEmpty(currentStartTime)
            && !string.Equals(recordedStartTime, currentStartTime, StringComparison.Ordinal))
        {
            AuditLogger.Warn(
                $"拒绝终止 [{profile.Name}] {handle.JobId}：pid {processGroupId} 的启动时间"
                + $"已从 {recordedStartTime} 变为 {currentStartTime}，该 pid 已被其他进程复用");
            return (false, "pid 已被其他进程复用，已放弃终止以避免误杀。请人工确认后清理。");
        }

        ExecuteShell(profile, RemoteJobScripts.BuildKillCommand(processGroupId.Value, force),
            ShortCommandTimeoutMilliseconds);

        // 1.2 打标记，让状态查询能区分「手动终止」和「异常终止」
        try
        {
            sftp.WriteAllText(directory + "/terminated", DateTime.UtcNow.ToString("O"), Utf8NoBom);
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"写入终止标记失败 {handle.JobId}: {exception.Message}");
        }

        AuditLogger.Info($"任务已终止 [{profile.Name}] {handle.JobId} pid={processGroupId} force={force}");
        return (true, $"已终止任务 {handle.JobId}（进程组 {processGroupId}）");
    }

    /// <summary>
    /// 退出时杀掉本进程启动过的所有仍在运行的任务
    /// </summary>
    public void KillAllStarted()
    {
        List<(HostProfile Profile, RemoteJobHandle Handle)> snapshot;
        lock (_syncRoot)
        {
            snapshot = _startedJobs.ToList();
            _startedJobs.Clear();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        AuditLogger.Info($"服务退出，准备终止 {snapshot.Count} 个远程任务");
        foreach (var (profile, handle) in snapshot)
        {
            try
            {
                using var lease = _pool.Acquire(profile);
                var sftp = lease.Client;
                var files = ListJobDirectory(sftp, handle.RemoteDirectory);
                if (files.Count == 0 || files.ContainsKey("exit"))
                {
                    continue;
                }

                var processGroupId = ReadProcessGroupId(sftp, handle.RemoteDirectory);
                if (processGroupId is null || !IsProcessAlive(sftp, processGroupId.Value))
                {
                    continue;
                }

                // 退出路径用 KILL，避免每个任务多等 3 秒的 TERM 宽限期
                ExecuteShell(
                    profile,
                    RemoteJobScripts.BuildKillCommand(processGroupId.Value, force: true),
                    ShortCommandTimeoutMilliseconds);
                AuditLogger.Info($"退出清理已终止任务 [{profile.Name}] {handle.JobId} pid={processGroupId}");
            }
            catch (Exception exception)
            {
                // 进程正在退出，这里失败只能记录，没有重试机会
                AuditLogger.Warn($"退出清理任务 {handle.JobId} 失败: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
    }

    /// <summary>
    /// 组装任务状态
    /// </summary>
    private RemoteJobInfo BuildStatus(SftpClient sftp, string jobId, string directory)
    {
        var files = ListJobDirectory(sftp, directory);
        if (files.Count == 0)
        {
            return new RemoteJobInfo
            {
                JobId = jobId,
                RemoteDirectory = directory,
                State = RemoteJobState.Lost
            };
        }

        var command = (TryReadText(sftp, directory + "/cmd") ?? string.Empty).Trim();
        var stdoutBytes = files.TryGetValue("out", out var outFile) ? outFile.Length : 0;
        var stderrBytes = files.TryGetValue("err", out var errFile) ? errFile.Length : 0;
        var startedAtUtc = files.TryGetValue("pid", out var pidFile) ? pidFile.LastWriteTimeUtc : (DateTime?)null;

        // 1.1 有退出码就是正常结束
        if (files.ContainsKey("exit"))
        {
            var raw = TryReadText(sftp, directory + "/exit")?.Trim();
            var exitCode = int.TryParse(raw, out var parsed) ? parsed : (int?)null;
            return new RemoteJobInfo
            {
                JobId = jobId,
                RemoteDirectory = directory,
                Command = command,
                State = RemoteJobState.Completed,
                ExitCode = exitCode,
                StartedAtUtc = startedAtUtc,
                StdoutBytes = stdoutBytes,
                StderrBytes = stderrBytes
            };
        }

        // 1.2 没有退出码时看终止标记，再看进程是否还活着
        var state = ResolveRunningState(sftp, directory, files);
        return new RemoteJobInfo
        {
            JobId = jobId,
            RemoteDirectory = directory,
            Command = command,
            State = state,
            StartedAtUtc = startedAtUtc,
            StdoutBytes = stdoutBytes,
            StderrBytes = stderrBytes,
            Elapsed = startedAtUtc is null ? null : DateTime.UtcNow - startedAtUtc.Value
        };
    }

    /// <summary>
    /// 没有退出码时的状态判定
    /// </summary>
    private static RemoteJobState ResolveRunningState(
        SftpClient sftp, string directory, Dictionary<string, ISftpFile> files)
    {
        if (files.ContainsKey("timeout"))
        {
            return RemoteJobState.TimedOut;
        }
        if (files.ContainsKey("terminated"))
        {
            return RemoteJobState.Terminated;
        }

        var processGroupId = ReadProcessGroupId(sftp, directory);
        if (processGroupId is not null && IsProcessAlive(sftp, processGroupId.Value))
        {
            return RemoteJobState.Running;
        }
        return RemoteJobState.Lost;
    }

    /// <summary>
    /// 增量读取一段输出
    /// </summary>
    private static JobOutputChunk ReadChunk(
        SftpClient sftp, string path, long offset, int maxBytes, string encodingName)
    {
        try
        {
            var (content, totalLength, truncated) = RemoteFileReader.Read(sftp, path, offset, -1, maxBytes);
            if (content.Length == 0)
            {
                return new JobOutputChunk(string.Empty, offset, false);
            }

            var decoded = RemoteFileReader.DecodeText(content, encodingName, truncated);
            var text = decoded.Success
                ? decoded.Text ?? string.Empty
                : decoded.IsBinary
                    ? $"[二进制内容，{content.Length} 字节，无法以文本展示]"
                    : $"[无法按 {decoded.EncodingName} 解码，{content.Length} 字节]";
            return new JobOutputChunk(text, offset + content.Length, offset + content.Length < totalLength);
        }
        catch (Exception)
        {
            // 输出文件还没生成或已被清理，按无新增处理
            return new JobOutputChunk(string.Empty, offset, false);
        }
    }

    /// <summary>
    /// 读取整份输出，超过上限时截断
    /// </summary>
    private static (string Text, bool Truncated) ReadTextOrEmpty(
        SftpClient sftp, string path, int maxBytes, string encodingName)
    {
        try
        {
            var (content, _, truncated) = RemoteFileReader.Read(sftp, path, 0, -1, maxBytes);
            var decoded = RemoteFileReader.DecodeText(content, encodingName, truncated);
            return (decoded.Success ? decoded.Text ?? string.Empty : string.Empty, truncated);
        }
        catch (Exception)
        {
            // 文件还不存在或读不到，按无输出处理
            return (string.Empty, false);
        }
    }

    /// <summary>
    /// 解析主机环境：家目录、登录 shell、是否支持 setsid。结果按主机缓存
    /// </summary>
    private HostEnvironment ResolveEnvironment(HostProfile profile, SftpClient sftp)
    {
        if (_environments.TryGetValue(profile.Name, out var cached))
        {
            return cached;
        }

        // 1.1 SFTP 的默认工作目录通常就是家目录，避免为此额外开一次 SSH
        string homeDirectory;
        try
        {
            homeDirectory = sftp.WorkingDirectory;
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取主机 {profile.Name} 默认目录失败: {exception.Message}");
            homeDirectory = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(homeDirectory) || homeDirectory == "/")
        {
            // 兜底：默认目录拿不到时，直接用 /tmp 建任务目录
            homeDirectory = "/tmp";
            AuditLogger.Warn($"主机 {profile.Name} 无法确定家目录，任务目录改为 {homeDirectory}");
        }

        var shellPath = SafeExists(sftp, "/bin/bash") ? "/bin/bash" : "/bin/sh";
        var useSetsid = SafeExists(sftp, "/usr/bin/setsid") || SafeExists(sftp, "/bin/setsid");
        if (!useSetsid)
        {
            AuditLogger.Warn(
                $"主机 {profile.Name} 没有 setsid，任务将失去会话隔离，SSH 通道异常断开时可能收不到终止信号");
        }

        var environment = new HostEnvironment(
            homeDirectory,
            homeDirectory.TrimEnd('/') + "/.remote-fs/jobs",
            shellPath,
            useSetsid);
        _environments[profile.Name] = environment;
        return environment;
    }

    /// <summary>
    /// 等 pid 文件出现，返回进程组 id。
    /// 在服务器上等，1 次往返拿结果；原来每 100ms 轮询一次 SFTP，最多 50 次往返。
    /// </summary>
    private int? WaitForProcessGroupId(HostProfile profile, SftpClient sftp, string directory)
    {
        var attempts = Math.Max(1, StartConfirmationMilliseconds / 100);
        var command = RemoteJobScripts.BuildWaitForPidCommand(directory + "/pid", attempts);

        try
        {
            using var lease = _sshPool.Acquire(profile, out _);
            using var runner = lease.Client.CreateCommand(command);
            runner.CommandTimeout = TimeSpan.FromMilliseconds(StartConfirmationMilliseconds + 2_000);
            runner.Execute();

            var output = runner.Result?.Trim();
            return int.TryParse(output, out var parsed) && parsed > 1 ? parsed : null;
        }
        catch (Agent.SshUnavailableException)
        {
            // SSH 整个连不上，要让上层改走备用端口，不能在这里吞掉
            throw;
        }
        catch (Exception exception)
        {
            // 通道出问题不该让任务提交直接失败，退回轮询再试一次
            AuditLogger.Warn($"等待任务 pid 失败，改用轮询: {exception.Message}");
            return WaitForProcessGroupIdByPolling(sftp, directory);
        }
    }

    /// <summary>
    /// 轮询等待 pid 文件，通道不可用时的兜底
    /// </summary>
    private static int? WaitForProcessGroupIdByPolling(SftpClient sftp, string directory)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(StartConfirmationMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var processGroupId = ReadProcessGroupId(sftp, directory);
            if (processGroupId is not null)
            {
                return processGroupId;
            }
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>
    /// 读取进程组 id
    /// </summary>
    private static int? ReadProcessGroupId(SftpClient sftp, string directory)
    {
        var raw = TryReadText(sftp, directory + "/pid")?.Trim();
        return int.TryParse(raw, out var parsed) && parsed > 1 ? parsed : null;
    }

    /// <summary>
    /// 读取进程启动时间（/proc/pid/stat 第 22 字段），用于识别 pid 复用
    /// </summary>
    private static string? ReadProcessStartTime(SftpClient sftp, int processId)
    {
        var raw = TryReadText(sftp, $"/proc/{processId}/stat");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 字段 2 是带括号的命令名，先跳过它再数，避免命令名里带空格时错位
        var closeIndex = raw.LastIndexOf(')');
        if (closeIndex < 0)
        {
            return null;
        }

        var fields = raw[(closeIndex + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 跳过 ')' 之后的空格后，fields[0] 对应 stat 的第 3 字段，第 22 字段在 fields[19]
        return fields.Length > 19 ? fields[19] : null;
    }

    /// <summary>
    /// 进程是否存活
    /// </summary>
    private static bool IsProcessAlive(SftpClient sftp, int processId)
    {
        return SafeExists(sftp, $"/proc/{processId}");
    }

    /// <summary>
    /// 列出任务目录内的文件
    /// </summary>
    private static Dictionary<string, ISftpFile> ListJobDirectory(SftpClient sftp, string directory)
    {
        try
        {
            return sftp.ListDirectory(directory)
                .Where(entry => entry.Name is not ("." or ".."))
                .ToDictionary(entry => entry.Name, entry => entry, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 目录不存在或不可读，按空处理
            return new Dictionary<string, ISftpFile>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 存在性判断，异常一律按不存在处理
    /// </summary>
    private static bool SafeExists(SftpClient sftp, string path)
    {
        try
        {
            return sftp.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取小文本文件，失败返回 null。
    /// 不做存在性预判，直接读后兜异常，少一次往返
    /// </summary>
    private static string? TryReadText(SftpClient sftp, string path)
    {
        try
        {
            return sftp.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 下发启动命令。
    /// 后台任务会一直持有 SSH 通道直到命令结束，等它自然关闭会把启动耗时拖成任务时长，
    /// 所以这里只等命令送达就收手：任务已经脱离会话，通道提前关闭不会波及它，
    /// 是否真的跑起来由后续的 pid 文件确认。
    /// </summary>
    private void ExecuteLaunchCommand(HostProfile profile, string command)
    {
        try
        {
            using var lease = _sshPool.Acquire(profile, out _);
            using var runner = lease.Client.CreateCommand(command);
            runner.CommandTimeout = TimeSpan.FromMilliseconds(LaunchConfirmationMilliseconds);
            runner.Execute();
        }
        catch (Agent.SshUnavailableException)
        {
            // SSH 整个连不上，要让上层改走备用端口，不能在这里吞掉
            throw;
        }
        catch (Exception exception)
        {
            AuditLogger.Debug($"启动命令通道提前结束（不影响已脱session的任务）: {exception.Message}");
        }
    }

    /// <summary>
    /// 执行短命令，不抛异常，失败只记录
    /// </summary>
    private void ExecuteShell(HostProfile profile, string command, int timeoutMilliseconds)
    {
        try
        {
            using var lease = _sshPool.Acquire(profile, out _);
            using var runner = lease.Client.CreateCommand(command);
            runner.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
            runner.Execute();

            if (runner.ExitStatus is not null and not 0)
            {
                AuditLogger.Warn(
                    $"远程命令退出码 {runner.ExitStatus}: {command}，错误输出 {runner.Error?.Trim()}");
            }
        }
        catch (Agent.SshUnavailableException)
        {
            // SSH 整个连不上，要让上层改走备用端口，不能在这里吞掉
            throw;
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"远程命令执行失败: {command}，原因 {exception.Message}");
        }
    }

    /// <summary>
    /// 记录本进程提交过的任务，退出时按此清单清理
    /// </summary>
    private void RememberStarted(HostProfile profile, RemoteJobHandle handle)
    {
        lock (_syncRoot)
        {
            _startedJobs.Add((profile, handle));
        }
    }

    /// <summary>
    /// 按节流周期触发保留策略清理
    /// </summary>
    private void CleanupRetentionIfDue(HostProfile profile, SftpClient sftp)
    {
        var now = DateTime.UtcNow;
        if (_lastCleanupUtc.TryGetValue(profile.Name, out var last) && now - last < CleanupInterval)
        {
            return;
        }
        _lastCleanupUtc[profile.Name] = now;

        var environment = ResolveEnvironment(profile, sftp);
        if (!SafeExists(sftp, environment.JobRoot))
        {
            return;
        }

        try
        {
            var directories = sftp.ListDirectory(environment.JobRoot)
                .Where(entry => entry.IsDirectory && entry.Name.StartsWith("job-", StringComparison.Ordinal))
                .ToList();
            CleanupRetention(profile, sftp, directories);
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"任务目录清理失败: {exception.Message}");
        }
    }

    /// <summary>
    /// 清理超期或超量的已结束任务目录
    /// </summary>
    private void CleanupRetention(
        HostProfile profile, SftpClient sftp, List<ISftpFile> directories)
    {
        var expiredBefore = DateTime.UtcNow.AddDays(-Math.Max(1, _config.JobRetentionDays));
        var keepCount = Math.Max(1, _config.JobMaxCount);

        // 1.1 先按时间和数量挑出候选，避免逐个任务目录都列一遍内容
        var candidates = new List<ISftpFile>();
        var index = 0;
        foreach (var directory in directories.OrderByDescending(item => item.LastWriteTimeUtc))
        {
            index++;
            if (index > keepCount || directory.LastWriteTimeUtc < expiredBefore)
            {
                candidates.Add(directory);
            }
        }

        // 1.2 只有已经结束的任务才真正删除，运行中的一律保留
        var removable = new List<ISftpFile>();
        foreach (var directory in candidates)
        {
            var files = ListJobDirectory(sftp, directory.FullName);
            if (files.ContainsKey("exit") || files.ContainsKey("timeout") || files.ContainsKey("terminated"))
            {
                removable.Add(directory);
            }
        }

        if (removable.Count == 0)
        {
            return;
        }

        // 1.3 逐条删除，用服务端 rm -rf 而不是走 SFTP 递归，快得多
        var removed = 0;
        foreach (var directory in removable)
        {
            try
            {
                using var lease = _sshPool.Acquire(profile, out _);
                using var runner = lease.Client.CreateCommand($"rm -rf -- {ShellQuoting.Quote(directory.FullName)}");
                runner.CommandTimeout = TimeSpan.FromMilliseconds(ShortCommandTimeoutMilliseconds);
                runner.Execute();
                removed++;
            }
            catch (Exception exception)
            {
                AuditLogger.Warn($"清理任务目录 {directory.FullName} 失败: {exception.Message}");
            }
        }

        if (removed > 0)
        {
            AuditLogger.Info($"已清理 {removed} 个已结束的远程任务目录（保留上限 {keepCount} 个）");
        }
    }

    /// <summary>
    /// 生成任务目录名，包含时间与随机后缀避免同一秒内重名
    /// </summary>
    private static string BuildJobId()
    {
        return "job-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-"
            + Guid.NewGuid().ToString("N")[..4];
    }

    /// <summary>
    /// 主机环境：家目录、任务根目录、登录 shell、是否支持 setsid
    /// </summary>
    private sealed record HostEnvironment(
        string HomeDirectory, string JobRoot, string ShellPath, bool UseSetsid);
}

/// <summary>
/// 前台命令的执行结果
/// </summary>
public sealed record ForegroundJobResult(
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool Truncated,
    RemoteJobHandle Handle);