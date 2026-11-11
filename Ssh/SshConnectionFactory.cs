using RemoteFs.Common;
using RemoteFs.Config;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace RemoteFs.Ssh;

/// <summary>
/// SSH 连接工厂：按主机配置建立 SSH 与 SFTP 连接
/// </summary>
public sealed class SshConnectionFactory
{
    private readonly AppConfig _config;

    public SshConnectionFactory(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 创建并连接 SSH 客户端，指纹通过 <paramref name="fingerprint"/> 返回
    /// </summary>
    public SshClient CreateSshClient(HostProfile profile, out string fingerprint)
    {
        var client = new SshClient(BuildConnectionInfo(profile));
        var capture = new HostKeyCapture();
        AttachHostKeyCheck(profile, client, capture);

        try
        {
            client.Connect();
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw BuildConnectionFailure(profile, capture, exception);
        }

        fingerprint = FormatFingerprint(capture.Fingerprint);
        AuditLogger.Info($"已连接 {profile.Name} ({profile.Username}@{profile.Host}:{profile.Port}) 指纹 {fingerprint}");
        return client;
    }

    /// <summary>
    /// 创建并连接 SFTP 客户端
    /// </summary>
    public SftpClient CreateSftpClient(HostProfile profile)
    {
        var client = new SftpClient(BuildConnectionInfo(profile));
        client.OperationTimeout = TimeSpan.FromMilliseconds(_config.SftpOperationTimeoutMilliseconds);
        var capture = new HostKeyCapture();
        AttachHostKeyCheck(profile, client, capture);

        try
        {
            client.Connect();
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw BuildConnectionFailure(profile, capture, exception);
        }

        return client;
    }

    /// <summary>
    /// 构造连接信息，按配置组装认证方式（先密钥后密码）
    /// </summary>
    private ConnectionInfo BuildConnectionInfo(HostProfile profile)
    {
        var authenticationMethods = new List<AuthenticationMethod>();

        var keyFile = LoadPrivateKey(profile);
        if (keyFile is not null)
        {
            authenticationMethods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
        }

        if (profile.Auth is AuthMethod.Password or AuthMethod.Auto)
        {
            var password = profile.ResolvePassword();
            if (!string.IsNullOrEmpty(password))
            {
                authenticationMethods.Add(new PasswordAuthenticationMethod(profile.Username, password));
            }
        }

        if (authenticationMethods.Count == 0)
        {
            throw new UnauthorizedAccessException(
                $"主机 {profile.Name} 未配置可用凭据：请提供私钥文件或密码。");
        }

        var connectionInfo = new ConnectionInfo(
            profile.Host, profile.Port, profile.Username, authenticationMethods.ToArray())
        {
            Timeout = TimeSpan.FromMilliseconds(_config.ConnectionTimeoutMilliseconds)
        };

        return connectionInfo;
    }

    /// <summary>
    /// 挂载主机密钥校验：指纹不匹配或未固化指纹时置 CanTrust=false，握手阶段即拒绝
    /// </summary>
    private static void AttachHostKeyCheck(HostProfile profile, BaseClient client, HostKeyCapture capture)
    {
        client.HostKeyReceived += (_, eventArgs) =>
        {
            capture.Fingerprint = eventArgs.FingerPrintSHA256;

            // 1.1 未配置期望指纹
            if (string.IsNullOrWhiteSpace(profile.KnownHostKeyFingerprint))
            {
                if (profile.StrictHostKeyChecking)
                {
                    // 开了强校验却没固化指纹，拒绝放行，避免出现「以为校验了其实没有」
                    capture.RejectionReason = "missing-fingerprint";
                    eventArgs.CanTrust = false;
                    return;
                }

                // 关闭强校验时按首次连接信任（TOFU），仅供内网或临时排障使用
                AuditLogger.Warn(
                    $"主机 {profile.Name} 未启用指纹校验，已按首次连接信任实际指纹 {eventArgs.FingerPrintSHA256}");
                eventArgs.CanTrust = true;
                return;
            }

            // 1.2 比对已固化的指纹（两种写法都兼容：带 SHA256: 前缀或不带）
            var matched = string.Equals(
                NormalizeFingerprint(profile.KnownHostKeyFingerprint),
                NormalizeFingerprint(eventArgs.FingerPrintSHA256),
                StringComparison.Ordinal);
            if (!matched)
            {
                capture.RejectionReason = "fingerprint-mismatch";
            }
            eventArgs.CanTrust = matched;
        };
    }

    /// <summary>
    /// 构造可读的连接失败异常
    /// </summary>
    private static Exception BuildConnectionFailure(
        HostProfile profile, HostKeyCapture capture, Exception inner)
    {
        var actual = FormatFingerprint(capture.Fingerprint);

        // 1.1 开了强校验但还没固化指纹
        if (capture.RejectionReason == "missing-fingerprint")
        {
            return new InvalidOperationException(
                $"主机 {profile.Name} 启用了 strictHostKeyChecking，但未配置 knownHostKeyFingerprint。"
                + $"请执行 remote-fs hosts fingerprint {profile.Name} 取到指纹（本次握手实际指纹 {actual}）"
                + "填入配置后重试，或把该主机的 strictHostKeyChecking 设为 false。", inner);
        }

        // 1.2 指纹与固化值不一致
        if (capture.RejectionReason == "fingerprint-mismatch")
        {
            return new InvalidOperationException(
                $"主机 {profile.Name} 的密钥指纹不匹配。期望 {profile.KnownHostKeyFingerprint}，实际 {actual}。"
                + $"若服务器已重装，请执行 remote-fs hosts fingerprint {profile.Name} 更新配置。", inner);
        }

        return new InvalidOperationException(
            $"连接 {profile.Username}@{profile.Host}:{profile.Port} 失败: {inner.Message}", inner);
    }

    /// <summary>
    /// 加载私钥，支持无口令与有口令两种情况
    /// </summary>
    private static PrivateKeyFile? LoadPrivateKey(HostProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            return null;
        }

        var path = Path.GetFullPath(ExpandHome(profile.PrivateKeyPath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"私钥文件不存在: {path}", path);
        }

        try
        {
            return string.IsNullOrWhiteSpace(profile.PrivateKeyPassphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, profile.PrivateKeyPassphrase);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"私钥加载失败 ({path}): {exception.Message}。若私钥带口令，请配置 privateKeyPassphrase。", exception);
        }
    }

    /// <summary>
    /// 归一化指纹：SSH.NET 返回的是裸 base64，而 ssh-keygen 显示带 SHA256: 前缀，
    /// 比较时统一去掉前缀与补位等号，两种写法都认。
    /// </summary>
    public static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }
        return value.TrimEnd('=').Trim();
    }

    /// <summary>
    /// 归一化并以 ssh-keygen 的格式展示，方便和服务器控制台核对
    /// </summary>
    public static string FormatFingerprint(string? fingerprint)
    {
        var normalized = NormalizeFingerprint(fingerprint);
        return normalized.Length == 0 ? "未知" : "SHA256:" + normalized;
    }

    /// <summary>
    /// 展开路径中的 ~ 与环境变量
    /// </summary>
    public static string ExpandHome(string path)
    {
        var result = Environment.ExpandEnvironmentVariables(path);
        if (result.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            result = Path.Combine(home, result.TrimStart('~', '/', '\\'));
        }
        return result;
    }

    /// <summary>
    /// 握手期间捕获的主机密钥指纹与拒绝原因
    /// </summary>
    private sealed class HostKeyCapture
    {
        public string? Fingerprint { get; set; }

        public string? RejectionReason { get; set; }
    }
}