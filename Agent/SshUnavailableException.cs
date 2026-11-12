using RemoteFs.Config;
using RemoteFs.Ssh;

namespace RemoteFs.Agent;

/// <summary>
/// SSH 在连接阶段就失败。调用方可以据此改走备用端口，而不是把认证失败、指纹不符也当成通道中断。
/// </summary>
public sealed class SshUnavailableException : Exception
{
    public SshUnavailableException(HostProfile profile, Exception inner)
        : base(inner.Message, inner)
    {
        Profile = profile;
    }

    /// <summary>这次没连上的主机</summary>
    public HostProfile Profile { get; }

    /// <summary>
    /// 是否属于「端口或握手不可用」。认证被拒、私钥打不开、指纹不符都不算。
    /// </summary>
    public static bool IsTransportDown(Exception exception)
    {
        if (ConnectionDiagnostics.IsAuthenticationFailure(exception)
            || ConnectionDiagnostics.IsCredentialFailure(exception))
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("指纹", StringComparison.Ordinal)
                || current.Message.Contains("Host key", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return ConnectionDiagnostics.IsConnectStageFailure(exception);
    }
}
