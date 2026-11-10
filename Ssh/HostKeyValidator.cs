using RemoteFs.Config;
using Renci.SshNet;

namespace RemoteFs.Ssh;

/// <summary>
/// 主机密钥指纹工具
/// </summary>
public static class HostKeyValidator
{
    /// <summary>
    /// 临时连接一次以获取服务器指纹，用于首次配置固化
    /// </summary>
    public static string ProbeFingerprint(HostProfile profile, SshConnectionFactory factory)
    {
        using var client = factory.CreateSshClient(profile, out var fingerprint);
        return fingerprint;
    }
}