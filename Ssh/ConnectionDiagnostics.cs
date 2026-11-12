using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteFs.Common;
using RemoteFs.Config;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace RemoteFs.Ssh;

/// <summary>连接失败的诊断层次</summary>
public enum DiagnosticStage
{
    /// <summary>域名解析</summary>
    Dns,

    /// <summary>端口连通性</summary>
    Tcp,

    /// <summary>端口通但拿不到 SSH 握手</summary>
    Banner,

    /// <summary>主机密钥指纹</summary>
    HostKey,

    /// <summary>用户认证</summary>
    Authentication,

    /// <summary>无法归类</summary>
    Unknown
}

/// <summary>单层诊断结果</summary>
/// <remarks>
/// Detail 以「通过：」「失败：」「未执行：」开头，调用方可直接展示，
/// 也可用 <see cref="Passed"/> 判断该层是否通过。
/// </remarks>
public sealed record DiagnosticResult(
    DiagnosticStage Stage,
    string Detail,        // 该层的观测结果
    string Advice,        // 给人的处置建议
    string? Suggestion = null)  // 可选的替代做法
{
    /// <summary>该层是否通过</summary>
    public bool Passed => Detail.StartsWith("通过", StringComparison.Ordinal);
}

/// <summary>
/// SSH 连接失败的分层诊断：逐层判断连接卡在哪一环，并给出处置建议。
/// </summary>
public static class ConnectionDiagnostics
{
    /// <summary>TCP 探测超时时间（毫秒）</summary>
    private const int TcpProbeTimeoutMilliseconds = 3000;

    /// <summary>诊断时 SSH 握手等待上限（毫秒），避免诊断本身长时间挂住</summary>
    private const int ProbeHandshakeTimeoutMilliseconds = 15_000;

    /// <summary>
    /// 逐层探测连接情况：DNS → TCP → SSH 握手 → 指纹 → 认证。
    /// 供 remote_test_connection 使用，会真的建立一次连接。
    /// </summary>
    public static IReadOnlyList<DiagnosticResult> Probe(HostProfile profile)
    {
        var results = new List<DiagnosticResult>();

        // 1.1 DNS 解析
        var dns = ProbeDns(profile);
        results.Add(ToResult(dns));

        // 1.2 TCP 端口连通性：DNS 不通就不用再拨端口
        var tcp = dns.Passed
            ? ProbeTcp(profile)
            : Skipped(DiagnosticStage.Tcp, "DNS 层未通过，未继续探测端口");
        results.Add(ToResult(tcp));

        // 1.3 SSH 握手 / 指纹 / 认证：前置层不通时无从谈起
        if (!dns.Passed || !tcp.Passed)
        {
            const string reason = "前置层未通过，未建立 SSH 连接";
            results.Add(ToResult(Skipped(DiagnosticStage.Banner, reason)));
            results.Add(ToResult(Skipped(DiagnosticStage.HostKey, reason)));
            results.Add(ToResult(Skipped(DiagnosticStage.Authentication, reason)));
            return results;
        }

        var handshake = ProbeHandshake(profile);
        results.Add(ToResult(handshake.Banner));
        results.Add(ToResult(handshake.HostKey));
        results.Add(ToResult(handshake.Authentication));
        return results;
    }

    /// <summary>
    /// 连接阶段失败时的分层诊断：先查 DNS，再做 3 秒 TCP 探测，据此判断卡在哪一层。
    /// 只在「根本没连上」时调用；认证失败、指纹不匹配不调用，避免多拨一次连接。
    /// </summary>
    internal static LayerFinding DiagnoseConnectStage(HostProfile profile, Exception inner)
    {
        var dns = ProbeDns(profile);
        if (!dns.Passed)
        {
            return new LayerFinding(dns.Stage, false, false, dns.Observation, dns.Advice, dns.Suggestion);
        }

        var tcp = ProbeTcp(profile);
        if (!tcp.Passed)
        {
            return new LayerFinding(tcp.Stage, false, false, tcp.Observation, tcp.Advice, tcp.Suggestion);
        }

        // DNS 与 TCP 都通，说明卡在 SSH 握手（例如端口不是 SSH 服务，或 sshd 限流）
        return new LayerFinding(
            DiagnosticStage.Banner,
            false,
            false,
            $"端口 {profile.Host}:{profile.Port} 能连通，但 SSH 握手失败：{inner.Message}",
            "端口通但拿不到 SSH 握手，可能该端口不是 SSH 服务，或 sshd 因 MaxStartups 限流在拒绝新连接。",
            "在服务器上查看 sshd 日志与 MaxStartups 配置");
    }

    /// <summary>诊断层次的中文名称，用于拼装异常消息</summary>
    internal static string StageLabel(DiagnosticStage stage)
    {
        return stage switch
        {
            DiagnosticStage.Dns => "DNS 层",
            DiagnosticStage.Tcp => "TCP 层",
            DiagnosticStage.Banner => "SSH 握手层",
            DiagnosticStage.HostKey => "主机密钥层",
            DiagnosticStage.Authentication => "认证层",
            _ => "未知层"
        };
    }

    /// <summary>异常链里是否包含指定类型的异常</summary>
    internal static bool ContainsException<TException>(Exception? exception) where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 是否是认证层面的失败：认证被拒、服务端直接断开或用户名非法。
    /// 这类失败异常本身已经能定位层次，不需要再做 DNS/TCP 探测。
    /// </summary>
    internal static bool IsAuthenticationFailure(Exception? exception)
    {
        if (ContainsException<SshAuthenticationException>(exception))
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SshConnectionException connectionException
                && connectionException.DisconnectReason
                    is DisconnectReason.NoMoreAuthenticationMethodsAvailable
                    or DisconnectReason.IllegalUserName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否是连接阶段（握手之前）的失败：这类失败才值得再做 DNS/TCP 探测。
    /// </summary>
    internal static bool IsConnectStageFailure(Exception? exception)
    {
        return ContainsException<SocketException>(exception)
            || ContainsException<SshConnectionException>(exception)
            || ContainsException<SshOperationTimeoutException>(exception);
    }

    /// <summary>
    /// 是否是读取凭据阶段就失败：私钥文件不存在、私钥口令不对、没有可用凭据。
    /// </summary>
    internal static bool IsCredentialFailure(Exception? exception)
    {
        if (exception is FileNotFoundException or UnauthorizedAccessException)
        {
            return true;
        }

        return exception is InvalidOperationException
            && exception.Message.Contains("私钥", StringComparison.Ordinal);
    }

    /// <summary>
    /// 认证失败时的处置建议，区分私钥路径、口令、公钥三种可能。
    /// </summary>
    internal static string BuildAuthenticationAdvice(HostProfile profile, Exception? exception)
    {
        var message = exception?.Message ?? string.Empty;

        if (message.Contains("PublicKey", StringComparison.OrdinalIgnoreCase))
        {
            return "认证被拒：可能是私钥路径或私钥口令有误，也可能是公钥没写进服务器的 authorized_keys。"
                + $"请核对 hosts.json 里的 privateKeyPath 与 privateKeyPassphrase，并确认公钥已写入服务器 ~/.ssh/authorized_keys。";
        }

        if (message.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            return "认证被拒：可能是登录口令错误，也可能是账号被锁定或禁止了密码登录。"
                + "请核对 hosts.json 里的 password，或 passwordEnvironmentVariable 指向的环境变量。";
        }

        var extra = string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
            ? "请核对 hosts.json 里的 password，或 passwordEnvironmentVariable 指向的环境变量。"
            : $"请核对 hosts.json 里的 privateKeyPath（{profile.PrivateKeyPath}）与 privateKeyPassphrase、password，"
                + "并确认公钥已写入服务器 ~/.ssh/authorized_keys。";

        return "认证被拒：可能是私钥路径或私钥口令有误、登录口令错误，也可能是公钥没写进服务器的 authorized_keys。" + extra;
    }

    /// <summary>
    /// 把连接失败拼成一眼能看出卡在哪一层的消息。
    /// </summary>
    internal static string FormatFailureMessage(
        HostProfile profile, DiagnosticStage stage, string observation, string advice, string? suggestion)
    {
        var builder = new StringBuilder();
        builder.Append($"连接 {profile.Username}@{profile.Host}:{profile.Port} 失败（{StageLabel(stage)}）：{observation}。");
        builder.AppendLine();
        builder.Append($"建议：{advice}");

        if (!string.IsNullOrWhiteSpace(suggestion))
        {
            builder.AppendLine();
            builder.Append(suggestion);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把连接失败拼成一眼能看出卡在哪一层的消息。
    /// </summary>
    internal static string FormatFailureMessage(HostProfile profile, LayerFinding finding)
    {
        return FormatFailureMessage(
            profile, finding.Stage, finding.Observation, finding.Advice, finding.Suggestion);
    }

    /// <summary>
    /// 一层的诊断结论。既用于对外的 DiagnosticResult，也用于内部串联。
    /// </summary>
    internal sealed record LayerFinding(
        DiagnosticStage Stage,
        bool Passed,
        bool Skipped,
        string Observation,
        string Advice,
        string? Suggestion);

    /// <summary>DNS 解析层</summary>
    private static LayerFinding ProbeDns(HostProfile profile)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(profile.Host);
            if (addresses.Length == 0)
            {
                return new LayerFinding(
                    DiagnosticStage.Dns, false, false,
                    $"域名 {profile.Host} 没有解析到任何地址",
                    "主机名解析不了，检查域名拼写或改用 IP。",
                    "确认本机 DNS 配置，或在 hosts.json 里直接写服务器 IP");
            }

            var addressText = string.Join("、", addresses.Select(address => address.ToString()));
            return new LayerFinding(
                DiagnosticStage.Dns, true, false,
                $"域名 {profile.Host} 解析到 {addressText}",
                "本层通过，无需处理。",
                null);
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"诊断 DNS 解析失败 主机 {profile.Name} 地址 {profile.Host}: {exception.Message}");
            return new LayerFinding(
                DiagnosticStage.Dns, false, false,
                $"域名 {profile.Host} 解析异常：{exception.Message}",
                "主机名解析不了，检查域名拼写或改用 IP。",
                "确认本机 DNS 配置，或在 hosts.json 里直接写服务器 IP");
        }
    }

    /// <summary>TCP 端口连通性层，探测超时 3 秒</summary>
    private static LayerFinding ProbeTcp(HostProfile profile)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(profile.Host, profile.Port);

            if (!connectTask.Wait(TcpProbeTimeoutMilliseconds))
            {
                // 超时后连接任务仍在后台挂着，这里收尾掉避免异常无人观察
                _ = connectTask.ContinueWith(
                    task => _ = task.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);

                stopwatch.Stop();
                AuditLogger.Warn(
                    $"诊断 TCP 探测超时 主机 {profile.Name} 地址 {profile.Host}:{profile.Port} 超时 {TcpProbeTimeoutMilliseconds} 毫秒");
                return new LayerFinding(
                    DiagnosticStage.Tcp, false, false,
                    $"端口 {profile.Host}:{profile.Port} 连接超时（{TcpProbeTimeoutMilliseconds} 毫秒内无响应）",
                    "端口连不通，可能是安全组/防火墙未放行，也可能是 sshd 没在监听。",
                    $"请从云控制台的安全组规则确认端口 {profile.Port} 已放行，再用 remote-fs hosts fingerprint {profile.Name} 核对指纹。");
            }

            connectTask.GetAwaiter().GetResult();
            stopwatch.Stop();

            return new LayerFinding(
                DiagnosticStage.Tcp, true, false,
                $"端口 {profile.Host}:{profile.Port} 连通，耗时 {stopwatch.ElapsedMilliseconds} 毫秒",
                "本层通过，无需处理。",
                null);
        }
        catch (AggregateException aggregateException)
        {
            return ClassifySocketFailure(profile, aggregateException.GetBaseException(), stopwatch);
        }
        catch (SocketException exception)
        {
            return ClassifySocketFailure(profile, exception, stopwatch);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            AuditLogger.Warn(
                $"诊断 TCP 探测异常 主机 {profile.Name} 地址 {profile.Host}:{profile.Port}: {exception.Message}");
            return new LayerFinding(
                DiagnosticStage.Tcp, false, false,
                $"端口 {profile.Host}:{profile.Port} 探测异常：{exception.Message}",
                "端口连不通，可能是安全组/防火墙未放行，也可能是 sshd 没在监听。",
                $"请从云控制台的安全组规则确认端口 {profile.Port} 已放行");
        }
    }

    /// <summary>把 socket 异常细分成拒绝连接、解析不到等其他情况</summary>
    private static LayerFinding ClassifySocketFailure(
        HostProfile profile, Exception exception, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        AuditLogger.Warn(
            $"诊断 TCP 探测失败 主机 {profile.Name} 地址 {profile.Host}:{profile.Port}: {exception.Message}");

        if (exception is SocketException socketException)
        {
            if (socketException.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return new LayerFinding(
                    DiagnosticStage.Tcp, false, false,
                    $"端口 {profile.Host}:{profile.Port} 拒绝连接",
                    "端口拒绝连接，通常是 sshd 没启动或端口写错了。",
                    "在服务器上执行 systemctl status sshd 与 ss -lntp 确认监听端口，并核对 hosts.json 里的 port");
            }

            if (socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
            {
                return new LayerFinding(
                    DiagnosticStage.Dns, false, false,
                    $"域名 {profile.Host} 解析不到（{socketException.SocketErrorCode}）",
                    "主机名解析不了，检查域名拼写或改用 IP。",
                    "确认本机 DNS 配置，或在 hosts.json 里直接写服务器 IP");
            }

            return new LayerFinding(
                DiagnosticStage.Tcp, false, false,
                $"端口 {profile.Host}:{profile.Port} 连接失败：{socketException.SocketErrorCode} {socketException.Message}",
                "端口连不通，可能是安全组/防火墙未放行，也可能是 sshd 没在监听。",
                $"请从云控制台的安全组规则确认端口 {profile.Port} 已放行");
        }

        return new LayerFinding(
            DiagnosticStage.Tcp, false, false,
            $"端口 {profile.Host}:{profile.Port} 连接失败：{exception.Message}",
            "端口连不通，可能是安全组/防火墙未放行，也可能是 sshd 没在监听。",
            $"请从云控制台的安全组规则确认端口 {profile.Port} 已放行");
    }

    /// <summary>SSH 握手 / 指纹 / 认证三层，用一次连接同时判定</summary>
    private static (LayerFinding Banner, LayerFinding HostKey, LayerFinding Authentication) ProbeHandshake(
        HostProfile profile)
    {
        var factory = new SshConnectionFactory(CreateProbeConfig());

        // 用放宽指纹校验的副本连接，否则「还没固化指纹」会让探测本身直接失败
        var outcome = factory.TryCreateSshClient(HostKeyValidator.CreateProbeProfile(profile));

        if (outcome.Client is not null)
        {
            var serverVersion = outcome.ServerVersion ?? "未知";
            outcome.Client.Dispose();

            return (
                new LayerFinding(
                    DiagnosticStage.Banner, true, false,
                    $"SSH 握手成功，服务器版本 {serverVersion}",
                    "本层通过，无需处理。",
                    null),
                CompareFingerprint(profile, outcome.Fingerprint),
                new LayerFinding(
                    DiagnosticStage.Authentication, true, false,
                    $"用户 {profile.Username} 认证成功",
                    "本层通过，无需处理。",
                    null));
        }

        var failure = outcome.Failure;

        // 读凭据就失败：还没走到握手这一步
        if (failure is not null && IsCredentialFailure(failure))
        {
            return (
                Skipped(DiagnosticStage.Banner, $"凭据读取失败，未进入 SSH 握手：{failure.Message}"),
                Skipped(DiagnosticStage.HostKey, "凭据读取失败，未取到主机密钥指纹"),
                new LayerFinding(
                    DiagnosticStage.Authentication, false, false,
                    $"凭据配置有误：{failure.Message}",
                    "可能是私钥路径或私钥口令有误、登录口令错误，或公钥没写进服务器的 authorized_keys。",
                    "核对 hosts.json 里的 privateKeyPath / privateKeyPassphrase / password；公钥主机确认已写入服务器 ~/.ssh/authorized_keys"));
        }

        // 认证被拒：握手本身是通的，问题在凭据
        if (failure is not null && IsAuthenticationFailure(failure))
        {
            return (
                new LayerFinding(
                    DiagnosticStage.Banner, true, false,
                    "SSH 握手成功，服务端已进入认证阶段",
                    "本层通过，无需处理。",
                    null),
                CompareFingerprint(profile, outcome.Fingerprint),
                new LayerFinding(
                    DiagnosticStage.Authentication, false, false,
                    $"认证被拒：{failure.Message}",
                    BuildAuthenticationAdvice(profile, failure),
                    null));
        }

        // 其余情况都算握手没成
        var reason = failure?.Message ?? "未知原因";
        return (
            new LayerFinding(
                DiagnosticStage.Banner, false, false,
                $"SSH 握手失败：{reason}",
                "端口通但拿不到 SSH 握手，可能该端口不是 SSH 服务，或 sshd 因 MaxStartups 限流在拒绝新连接。",
                "在服务器上查看 sshd 日志与 MaxStartups 配置"),
            Skipped(DiagnosticStage.HostKey, "SSH 握手未完成，未取到主机密钥指纹"),
            Skipped(DiagnosticStage.Authentication, "SSH 握手未完成，未进入认证阶段"));
    }

    /// <summary>比对已固化指纹与服务器实际指纹</summary>
    private static LayerFinding CompareFingerprint(HostProfile profile, string? actualFingerprint)
    {
        var actual = SshConnectionFactory.FormatFingerprint(actualFingerprint);

        if (string.IsNullOrWhiteSpace(profile.KnownHostKeyFingerprint))
        {
            return new LayerFinding(
                DiagnosticStage.HostKey, true, false,
                $"未配置 knownHostKeyFingerprint，本次不做比对（实际指纹 {actual}）",
                "未固化指纹，无法判断服务器是否被替换过。",
                $"执行 remote-fs hosts fingerprint {profile.Name} 取到指纹并写入 hosts.json");
        }

        var matched = string.Equals(
            SshConnectionFactory.NormalizeFingerprint(profile.KnownHostKeyFingerprint),
            SshConnectionFactory.NormalizeFingerprint(actualFingerprint),
            StringComparison.Ordinal);

        if (matched)
        {
            return new LayerFinding(
                DiagnosticStage.HostKey, true, false,
                $"指纹与配置一致（{actual}）",
                "本层通过，无需处理。",
                null);
        }

        return new LayerFinding(
            DiagnosticStage.HostKey, false, false,
            $"指纹不一致：配置 {profile.KnownHostKeyFingerprint}，实际 {actual}",
            "服务器可能重装过或被中间人劫持，确认无误后再更新配置。",
            $"执行 remote-fs hosts fingerprint {profile.Name} 更新配置");
    }

    /// <summary>该层没有执行</summary>
    private static LayerFinding Skipped(DiagnosticStage stage, string reason)
    {
        return new LayerFinding(stage, false, true, reason, "前置层未通过，本层未执行。", null);
    }

    /// <summary>把内部结论转成对外的诊断结果</summary>
    private static DiagnosticResult ToResult(LayerFinding finding)
    {
        var prefix = finding.Skipped ? "未执行：" : finding.Passed ? "通过：" : "失败：";
        return new DiagnosticResult(finding.Stage, prefix + finding.Observation, finding.Advice, finding.Suggestion);
    }

    /// <summary>探测用的配置：握手等待时间收紧，避免诊断本身长时间挂住</summary>
    private static AppConfig CreateProbeConfig()
    {
        AppConfig config;
        try
        {
            config = new ConfigStore().LoadConfig();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"诊断读取全局配置失败，改用默认超时: {exception.Message}");
            config = new AppConfig();
        }

        if (config.ConnectionTimeoutMilliseconds > ProbeHandshakeTimeoutMilliseconds)
        {
            config.ConnectionTimeoutMilliseconds = ProbeHandshakeTimeoutMilliseconds;
        }
        return config;
    }
}
