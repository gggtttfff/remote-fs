using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteFs.Common;

namespace RemoteFs.Mcp;

/// <summary>
/// 父进程看护。
/// MCP 服务本来靠 stdin EOF 退出的：客户端一关，管道写端关闭，我们读到的就是 EOF。
/// 但客户端被强杀、以隐藏控制台启动、或者管道写端被子进程继承时，EOF 不会到来，
/// 进程就永远留在后台占着 exe。这里再加一道父进程存活检查兜底。
/// 设置环境变量 REMOTE_FS_PARENT_WATCH=0 可以关掉。
/// </summary>
public static class ParentProcessWatchdog
{
    private const int PollIntervalMilliseconds = 2_000;

    /// <summary>连续多少次发现父进程不在了才真的退出，避免瞬间的采样误差</summary>
    private const int MissesBeforeExit = 3;

    private static bool _started;

    /// <summary>
    /// 启动看护线程。父进程消失后调用 <paramref name="onParentExited"/>
    /// </summary>
    public static void Start(Action onParentExited)
    {
        if (_started)
        {
            return;
        }
        _started = true;

        if (IsDisabled())
        {
            AuditLogger.Info("父进程看护已按配置关闭（REMOTE_FS_PARENT_WATCH=0）");
            return;
        }

        var parentProcessId = TryGetParentProcessId();
        if (parentProcessId is null)
        {
            // 拿不到父进程号环境很多（容器、特殊启动方式），这时只能继续依赖 stdin EOF
            AuditLogger.Warn("无法确定父进程，跳过父进程看护，仅依赖 stdin EOF 退出");
            return;
        }

        AuditLogger.Info($"父进程看护已启动，父进程 PID {parentProcessId}");

        var thread = new Thread(() => Watch(parentProcessId.Value, onParentExited))
        {
            IsBackground = true,
            Name = "parent-watchdog"
        };
        thread.Start();
    }

    /// <summary>
    /// 轮询父进程是否还活着
    /// </summary>
    private static void Watch(int parentProcessId, Action onParentExited)
    {
        var misses = 0;
        while (true)
        {
            Thread.Sleep(PollIntervalMilliseconds);

            if (IsParentAlive(parentProcessId))
            {
                misses = 0;
                continue;
            }

            misses++;
            if (misses < MissesBeforeExit)
            {
                continue;
            }

            AuditLogger.Warn($"父进程 PID {parentProcessId} 已退出，MCP 服务随之退出");
            onParentExited();
            return;
        }
    }

    /// <summary>
    /// 父进程是否仍在运行
    /// </summary>
    private static bool IsParentAlive(int parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            return !parent.HasExited;
        }
        catch (ArgumentException)
        {
            // 进程号查不到就是已经退出了
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception exception)
        {
            // 权限等问题拿不到状态时按"还活着"处理，宁可多留一会儿也不要误杀
            AuditLogger.Warn($"检查父进程状态失败: {exception.Message}");
            return true;
        }
    }

    private static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable("REMOTE_FS_PARENT_WATCH");
        return !string.IsNullOrWhiteSpace(value)
            && (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 取父进程号。Windows 走 Toolhelp 快照，类 Unix 用 getppid()
    /// </summary>
    private static int? TryGetParentProcessId()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsParentProcessId();
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var parentId = getppid();
            return parentId > 1 ? parentId : null;
        }
        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getppid();

    private static int? TryGetWindowsParentProcessId()
    {
        try
        {
            var currentProcessId = (uint)Environment.ProcessId;
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            {
                return null;
            }

            try
            {
                var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
                if (!Process32FirstW(snapshot, ref entry))
                {
                    return null;
                }

                do
                {
                    if (entry.th32ProcessID == currentProcessId)
                    {
                        return entry.th32ParentProcessID > 1
                            ? (int)entry.th32ParentProcessID
                            : null;
                    }
                    entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>();
                }
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"读取父进程号失败: {exception.Message}");
        }

        return null;
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}