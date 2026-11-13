using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Agent;

/// <summary>
/// 记住某台主机的 SSH 暂时不可用，避免每次工具调用都先空等握手超时。
/// </summary>
internal static class SshRouteCache
{
    /// <summary>一次连接阶段失败后，这段时间内不再尝试 SSH。</summary>
    public static readonly TimeSpan DownCooldown = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> DownUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 配了备用端口且应跳过 SSH 时直接抛出，让上层改走备用端口。
    /// </summary>
    public static void ThrowIfSkipped(HostProfile profile)
    {
        if (profile.AgentPort <= 0)
        {
            return;
        }

        if (profile.AgentFirst)
        {
            throw new SshUnavailableException(
                profile,
                $"主机 {profile.Name} 已设置优先使用备用端口 {profile.AgentPort}",
                alreadyKnown: true);
        }

        DateTime until;
        lock (Gate)
        {
            if (!DownUntilUtc.TryGetValue(profile.Name, out until) || until <= DateTime.UtcNow)
            {
                return;
            }
        }

        throw new SshUnavailableException(
            profile,
            $"主机 {profile.Name} 的 SSH 在 {until.ToLocalTime():HH:mm:ss} 前不再尝试，直接使用备用端口 {profile.AgentPort}",
            alreadyKnown: true);
    }

    /// <summary>SSH 连接阶段失败。只有配了备用端口才记，否则没有可切换的通道。</summary>
    public static void MarkDown(HostProfile profile)
    {
        if (profile.AgentPort <= 0)
        {
            return;
        }

        var until = DateTime.UtcNow.Add(DownCooldown);
        lock (Gate)
        {
            DownUntilUtc[profile.Name] = until;
        }

        AuditLogger.Info(
            $"主机 {profile.Name} 的 SSH 暂不可用，{DownCooldown.TotalMinutes:0} 分钟内直接走备用端口 {profile.AgentPort}");
    }

    /// <summary>SSH 已经连上，清掉不可用标记。</summary>
    public static void MarkAvailable(string hostName)
    {
        lock (Gate)
        {
            DownUntilUtc.Remove(hostName);
        }
    }
}
