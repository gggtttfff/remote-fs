using Renci.SshNet;
using Renci.SshNet.Security;
using RemoteFs.Config;
using RemoteFs.Ssh;

namespace RemoteFs.Agent;

/// <summary>
/// 备用通道沿用主机上已经配置的 SSH 私钥签名。
/// 服务器用运行用户的 authorized_keys 验签，不再另发一套密钥。
/// </summary>
internal static class AgentSshIdentity
{
    public static byte[] Sign(HostProfile profile, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"主机 {profile.Name} 未配置私钥。备用通道使用上面的私钥路径签名。");
        }

        var path = Path.GetFullPath(SshConnectionFactory.ExpandHome(profile.PrivateKeyPath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"私钥文件不存在: {path}", path);
        }

        using var privateKey = string.IsNullOrWhiteSpace(profile.PrivateKeyPassphrase)
            ? new PrivateKeyFile(path)
            : new PrivateKeyFile(path, profile.PrivateKeyPassphrase);
        return privateKey.Key.Sign(data);
    }

    public static IReadOnlyList<Key> LoadAuthorizedKeys(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys")
            : Path.GetFullPath(SshConnectionFactory.ExpandHome(configuredPath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到用于验签的公钥文件: {path}", path);
        }

        var keys = new List<Key>();
        foreach (var raw in File.ReadLines(path))
        {
            var key = TryParseAuthorizedKey(raw);
            if (key is not null)
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException($"{path} 里没有可用的公钥");
        }

        return keys;
    }

    public static bool VerifyAny(IReadOnlyList<Key> keys, byte[] data, byte[] signature)
    {
        foreach (var key in keys)
        {
            try
            {
                if (key.VerifySignature(data, signature))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 这一把公钥对不上，继续试下一把
            }
        }

        return false;
    }

    private static Key? TryParseAuthorizedKey(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var algorithmIndex = 0;
        if (!IsAlgorithm(parts[0]))
        {
            algorithmIndex = Array.FindIndex(parts, IsAlgorithm);
            if (algorithmIndex < 0 || algorithmIndex + 1 >= parts.Length)
            {
                return null;
            }
        }

        try
        {
            var blob = Convert.FromBase64String(parts[algorithmIndex + 1]);
            var data = new SshKeyData(blob);
            return data.Name switch
            {
                "ssh-ed25519" => new ED25519Key(data),
                "ssh-rsa" => new RsaKey(data),
                "ssh-dss" => new DsaKey(data),
                _ when data.Name.StartsWith("ecdsa-", StringComparison.Ordinal) => new EcdsaKey(data),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsAlgorithm(string token)
    {
        return token.StartsWith("ssh-", StringComparison.Ordinal)
            || token.StartsWith("ecdsa-", StringComparison.Ordinal);
    }
}
