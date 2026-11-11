namespace RemoteFs.Common;

/// <summary>
/// shell 引号处理：把任意字符串安全地拼进 POSIX shell 命令
/// </summary>
public static class ShellQuoting
{
    /// <summary>
    /// 用单引号包裹参数，内部单引号按 POSIX 规则转义
    /// </summary>
    public static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}