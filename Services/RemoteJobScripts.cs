using RemoteFs.Common;

namespace RemoteFs.Services;

/// <summary>
/// 远程任务脚本生成器。
/// 所有脚本一律用 \n 拼接并显式写入，绝不用 AppendLine：
/// Windows 上生成的 CRLF 会让远端的 sh 解析失败。
/// </summary>
public static class RemoteJobScripts
{
    /// <summary>
    /// 包装脚本：记录 pid 后执行用户命令，结束时写退出码。
    /// 刻意不 cd 到任务目录，保证相对路径仍相对于登录目录，与改造前的 remote_exec 一致。
    /// </summary>
    public static string BuildRunScript(string jobDirectory, string shellPath, bool useSetsid)
    {
        var job = ShellQuoting.Quote(jobDirectory);
        var shell = ShellQuoting.Quote(shellPath);
        var launcher = useSetsid ? "setsid sh" : "nohup sh";

        var lines = new[]
        {
            "#!/bin/sh",
            "# 由 remote-fs 生成的远程任务包装脚本，请勿手工修改",
            $"JOB={job}",
            $"SHELL_PATH={shell}",
            "exec >\"$JOB/out\" 2>\"$JOB/err\"",
            "echo $$ > \"$JOB/pid\"",
            "cut -d' ' -f22 /proc/$$/stat > \"$JOB/starttime\" 2>/dev/null",
            $"{launcher} \"$JOB/watchdog.sh\" </dev/null >/dev/null 2>&1 &",
            "\"$SHELL_PATH\" \"$JOB/cmd\"",
            "echo $? > \"$JOB/exit\""
        };
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// 超时看护脚本：任务一结束就退出，只有真的超时才动手终止整个进程组
    /// </summary>
    public static string BuildWatchdogScript(string jobDirectory, int ttlSeconds)
    {
        var job = ShellQuoting.Quote(jobDirectory);
        var deadline = Math.Max(1, ttlSeconds);

        var lines = new[]
        {
            "#!/bin/sh",
            "# 由 remote-fs 生成的超时看护脚本，任务结束即退出",
            $"JOB={job}",
            $"END=$(( $(date +%s) + {deadline} ))",
            "while [ \"$(date +%s)\" -lt \"$END\" ]; do",
            "  WATCHED_PID=$(cat \"$JOB/pid\" 2>/dev/null)",
            "  if [ -n \"$WATCHED_PID\" ] && ! kill -0 \"$WATCHED_PID\" 2>/dev/null; then",
            "    exit 0",
            "  fi",
            "  sleep 5",
            "done",
            "WATCHED_PID=$(cat \"$JOB/pid\" 2>/dev/null)",
            "if [ -n \"$WATCHED_PID\" ] && kill -0 \"$WATCHED_PID\" 2>/dev/null; then",
            "  touch \"$JOB/timeout\"",
            "  kill -TERM -- -\"$WATCHED_PID\" 2>/dev/null",
            "  sleep 5",
            "  kill -KILL -- -\"$WATCHED_PID\" 2>/dev/null",
            "fi"
        };
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// 启动脚本的 shell 片段：整个任务脱离当前会话，SSH 通道关闭不会波及它
    /// </summary>
    public static string BuildLaunchCommand(string jobDirectory, bool useSetsid)
    {
        var job = ShellQuoting.Quote(jobDirectory);
        var launcher = useSetsid ? "setsid sh" : "nohup sh";
        return $"mkdir -p {job} && {launcher} {job}/run.sh </dev/null >/dev/null 2>&1 &";
    }

    /// <summary>
    /// 终止命令：按进程组下发信号，负号表示整组
    /// </summary>
    public static string BuildKillCommand(int processGroupId, bool force)
    {
        if (force)
        {
            return $"kill -KILL -- -{processGroupId} 2>/dev/null; echo killed";
        }
        return $"kill -TERM -- -{processGroupId} 2>/dev/null; sleep 3; "
            + $"kill -KILL -- -{processGroupId} 2>/dev/null; echo killed";
    }

    /// <summary>
    /// 在服务器上等退出码文件出现，1 次往返拿结果。
    /// 文件一出现就立刻返回，客户端不必再轮询。
    /// </summary>
    public static string BuildWaitForExitCommand(string exitPath)
    {
        var exit = ShellQuoting.Quote(exitPath);
        return $"while [ ! -f {exit} ]; do sleep 0.2; done; cat {exit}";
    }

    /// <summary>
    /// 在服务器上等 pid 文件出现，1 次往返拿到进程组号。
    /// 超时后返回非零退出码，调用方按「没起来」处理。
    /// </summary>
    public static string BuildWaitForPidCommand(string pidPath, int attempts)
    {
        var pid = ShellQuoting.Quote(pidPath);
        var count = Math.Max(1, attempts);
        return $"i=0; while [ $i -lt {count} ]; do "
            + $"if [ -s {pid} ]; then cat {pid}; exit 0; fi; "
            + "i=$((i+1)); sleep 0.1; done; exit 1";
    }
}