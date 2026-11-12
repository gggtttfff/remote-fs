using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Ssh;

namespace RemoteFs.Gui;

/// <summary>
/// 主机配置校验器：GUI 保存前统一走这里，避免把不可用的配置写进 hosts.json。
/// </summary>
public static class HostProfileValidator
{
    /// <summary>
    /// 校验主机配置，返回全部错误信息；空列表表示通过。
    /// </summary>
    /// <param name="candidate">待保存的配置</param>
    /// <param name="existing">当前全部主机</param>
    /// <param name="editing">正在编辑的原配置，用于排除自身的别名重复检测</param>
    public static List<string> Validate(
        HostProfile candidate, IReadOnlyList<HostProfile> existing, HostProfile? editing)
    {
        var errors = new List<string>();

        // 1.1 基础字段
        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            errors.Add("别名不能为空");
        }
        if (string.IsNullOrWhiteSpace(candidate.Host))
        {
            errors.Add("主机地址不能为空");
        }
        if (string.IsNullOrWhiteSpace(candidate.Username))
        {
            errors.Add("登录用户名不能为空");
        }
        if (candidate.Port is < 1 or > 65535)
        {
            errors.Add("端口必须在 1-65535 之间");
        }
        if (candidate.CommandTimeoutMilliseconds < 1000)
        {
            errors.Add("命令超时不能小于 1000 毫秒");
        }

        // 1.2 别名唯一（排除自身）
        var duplicated = existing
            .Where(item => !ReferenceEquals(item, editing))
            .Any(item => string.Equals(item.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicated)
        {
            errors.Add($"别名 '{candidate.Name}' 已被其他主机占用");
        }

        // 2.1 认证凭据需与认证方式匹配
        var hasPrivateKey = !string.IsNullOrWhiteSpace(candidate.PrivateKeyPath);
        var hasPassword = !string.IsNullOrWhiteSpace(candidate.Password);
        var hasPasswordVariable = !string.IsNullOrWhiteSpace(candidate.PasswordEnvironmentVariable);
        switch (candidate.Auth)
        {
            case AuthMethod.Key when !hasPrivateKey:
                errors.Add("认证方式为密钥时必须填写私钥路径");
                break;
            case AuthMethod.Password when !hasPassword && !hasPasswordVariable:
                errors.Add("认证方式为密码时必须填写密码或密码环境变量名");
                break;
            case AuthMethod.Auto when !hasPrivateKey && !hasPassword && !hasPasswordVariable:
                errors.Add("认证方式为自动时，私钥与密码至少要配置一项");
                break;
        }

        // 2.2 私钥是本机文件，不存在则必然连接失败
        if (hasPrivateKey)
        {
            var keyPath = Path.GetFullPath(SshConnectionFactory.ExpandHome(candidate.PrivateKeyPath!));
            if (!File.Exists(keyPath))
            {
                errors.Add($"私钥文件不存在: {keyPath}");
            }
        }

        // 3.1 指纹格式：允许带或不带 SHA256: 前缀，其余部分应为 43 位 base64
        if (!string.IsNullOrWhiteSpace(candidate.KnownHostKeyFingerprint))
        {
            var normalized = SshConnectionFactory.NormalizeFingerprint(candidate.KnownHostKeyFingerprint);
            var isBase64 = normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '+' or '/');
            if (normalized.Length != 43 || !isBase64)
            {
                errors.Add("主机密钥指纹格式不正确，应为 43 位 base64，例如 SHA256:L69X5T... 或直接填 L69X5T...");
            }
        }

        // 3.2 白名单必须是绝对路径，相对路径在远程语义下无法确定基准
        foreach (var root in candidate.AllowedRoots)
        {
            if (!root.Replace('\\', '/').StartsWith('/'))
            {
                errors.Add($"允许根目录必须是绝对路径: {root}");
            }
        }

        // 3.3 默认编码：空值表示跟随全局，非空必须是受支持的编码名
        if (!string.IsNullOrWhiteSpace(candidate.DefaultEncoding)
            && !TextEncodingResolver.IsSupported(candidate.DefaultEncoding))
        {
            errors.Add($"默认编码 \"{candidate.DefaultEncoding}\" 不受支持，可用值："
                + string.Join("、", TextEncodingResolver.SupportedNames));
        }

        return errors;
    }
}