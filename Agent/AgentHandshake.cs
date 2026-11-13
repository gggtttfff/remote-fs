using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RemoteFs.Agent;

/// <summary>
/// 连接建立后做一次挑战应答。服务器出随机数，客户端用私钥签名，服务器用登记的公钥验签。
/// 签的是当次随机数，旧签名不能拿到下一条连接上重放。
/// </summary>
internal static class AgentHandshake
{
    public static void AuthenticateAsClient(Stream stream, Func<byte[], byte[]> sign, CancellationToken cancellationToken)
    {
        Write(stream, new JsonObject { ["type"] = "hello" }, cancellationToken);
        var challenge = ReadObject(stream, cancellationToken);
        var nonceText = challenge?["nonce"]?.GetValue<string>();
        if (challenge?["type"]?.GetValue<string>() != "challenge" || string.IsNullOrEmpty(nonceText))
        {
            throw new InvalidOperationException("备用端口没有给出认证挑战");
        }

        var nonce = Convert.FromBase64String(nonceText);
        var signature = sign(nonce);
        Write(stream, new JsonObject
        {
            ["type"] = "authenticate",
            ["signature"] = Convert.ToBase64String(signature)
        }, cancellationToken);

        var result = ReadObject(stream, cancellationToken);
        if (result?["ok"]?.GetValue<bool>() != true)
        {
            var text = result?["text"]?.GetValue<string>() ?? "认证失败";
            throw new InvalidOperationException(text);
        }
    }

    /// <summary>验签失败时写回错误并返回 false。调用方应关掉连接。</summary>
    public static bool AuthenticateAsServer(
        Stream stream, IReadOnlyList<Renci.SshNet.Security.Key> keys, CancellationToken cancellationToken)
    {
        var hello = ReadObject(stream, cancellationToken);
        if (hello?["type"]?.GetValue<string>() != "hello")
        {
            Write(stream, Fail("需要先完成私钥签名认证"), cancellationToken);
            return false;
        }

        var nonce = RandomNumberGenerator.GetBytes(32);
        Write(stream, new JsonObject
        {
            ["type"] = "challenge",
            ["nonce"] = Convert.ToBase64String(nonce)
        }, cancellationToken);

        var answer = ReadObject(stream, cancellationToken);
        var signatureText = answer?["signature"]?.GetValue<string>();
        if (answer?["type"]?.GetValue<string>() != "authenticate" || string.IsNullOrEmpty(signatureText))
        {
            Write(stream, Fail("认证材料不完整"), cancellationToken);
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            Write(stream, Fail("签名不是合法的 Base64"), cancellationToken);
            return false;
        }

        if (!AgentSshIdentity.VerifyAny(keys, nonce, signature))
        {
            Write(stream, Fail("私钥签名无法用 authorized_keys 验过"), cancellationToken);
            return false;
        }

        Write(stream, new JsonObject { ["ok"] = true, ["text"] = "已认证" }, cancellationToken);
        return true;
    }

    private static JsonObject Fail(string text)
    {
        return new JsonObject { ["ok"] = false, ["text"] = text };
    }

    private static void Write(Stream stream, JsonObject message, CancellationToken cancellationToken)
    {
        AgentFrame.WriteAsync(stream, message.ToJsonString(), cancellationToken).GetAwaiter().GetResult();
    }

    private static JsonObject? ReadObject(Stream stream, CancellationToken cancellationToken)
    {
        var json = AgentFrame.ReadAsync(stream, cancellationToken).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonNode.Parse(json) as JsonObject;
    }
}
