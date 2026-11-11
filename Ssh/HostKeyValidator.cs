using RemoteFs.Config;
using Renci.SshNet;

namespace RemoteFs.Ssh;

/// <summary>
/// 主机密钥指纹工具
/// </summary>
public static class HostKeyValidator
{
    /// <summary>
    /// 构造探测用的临时配置：关闭指纹比对。
    /// 否则「还没固化指纹」会把取指纹这个动作本身挡住，形成死锁。
    /// </summary>
    public static HostProfile CreateProbeProfile(HostProfile profile)
    {
        return new HostProfile
        {
            Name = profile.Name,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            Auth = profile.Auth,
            PrivateKeyPath = profile.PrivateKeyPath,
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
            Password = profile.Password,
            PasswordEnvironmentVariable = profile.PasswordEnvironmentVariable,
            StrictHostKeyChecking = false,
            KnownHostKeyFingerprint = null,
            AllowedRoots = new List<string>(profile.AllowedRoots),
            CommandTimeoutMilliseconds = profile.CommandTimeoutMilliseconds,
            Description = profile.Description
        };
    }

    /// <summary>
    /// 临时连接一次以获取服务器指纹，用于首次配置固化
    /// </summary>
    public static string ProbeFingerprint(HostProfile profile, SshConnectionFactory factory)
    {
        using var client = factory.CreateSshClient(CreateProbeProfile(profile), out var fingerprint);
        return fingerprint;
    }
}