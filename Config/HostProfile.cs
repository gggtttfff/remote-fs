namespace RemoteFs.Config;

/// <summary>
/// 认证方式
/// </summary>
public enum AuthMethod
{
    /// <summary>SSH 私钥文件认证</summary>
    Key,

    /// <summary>密码认证</summary>
    Password,

    /// <summary>两者都尝试，先密钥后密码</summary>
    Auto
}

/// <summary>
/// 单台远程主机的连接配置
/// </summary>
public sealed class HostProfile
{
    /// <summary>主机别名，工具调用时使用</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>实际地址</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    public AuthMethod Auth { get; set; } = AuthMethod.Auto;

    /// <summary>私钥文件路径</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>私钥密码（私钥有 passphrase 时需要）</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>登录密码</summary>
    public string? Password { get; set; }

    /// <summary>从环境变量读取登录密码，避免明文落盘</summary>
    public string? PasswordEnvironmentVariable { get; set; }

    /// <summary>是否启用 SSH 主机密钥指纹校验</summary>
    public bool StrictHostKeyChecking { get; set; } = true;

    /// <summary>已知主机密钥指纹（SHA256 base64），StrictHostKeyChecking 开启时用于校验</summary>
    public string? KnownHostKeyFingerprint { get; set; }

    /// <summary>允许访问的路径根目录。空列表表示不限制（会在日志中告警）</summary>
    public List<string> AllowedRoots { get; set; } = new();

    /// <summary>命令执行超时（毫秒）</summary>
    public int CommandTimeoutMilliseconds { get; set; } = 60_000;

    /// <summary>备注说明</summary>
    public string? Description { get; set; }

    /// <summary>读取登录密码，优先取环境变量</summary>
    public string? ResolvePassword()
    {
        if (!string.IsNullOrWhiteSpace(PasswordEnvironmentVariable))
        {
            var value = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return Password;
    }
}
