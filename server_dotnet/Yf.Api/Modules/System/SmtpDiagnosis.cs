using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.SystemManagement;

/// <summary>One step of an SMTP connection check: <c>OK</c>, <c>FAILED</c> or <c>SKIPPED</c> (an earlier step failed).</summary>
public sealed record SmtpDiagnosisStep(string Name, string Status, long ElapsedMs, string? Detail);

public sealed record SmtpDiagnosisResponse(bool Success, IReadOnlyList<SmtpDiagnosisStep> Steps);

internal interface ISmtpDiagnoser
{
    Task<IReadOnlyList<SmtpDiagnosisStep>> RunAsync(SmtpOptions options, ILogger logger, CancellationToken ct);
}

/// <summary>
/// Checks the saved SMTP settings step by step — TCP connection, TLS upgrade, login — and disconnects without sending
/// any mail. Details shown to the administrator carry only Chinese explanations, socket errors and SMTP status codes,
/// never credentials or the server's raw reply text.
/// </summary>
internal sealed class MailKitSmtpDiagnoser : ISmtpDiagnoser
{
    internal const string Connect = "连接";
    internal const string Encrypt = "加密";
    internal const string Login = "登录";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<SmtpDiagnosisStep>> RunAsync(SmtpOptions options, ILogger logger, CancellationToken ct)
    {
        var steps = new List<SmtpDiagnosisStep>();
        var watch = Stopwatch.StartNew();
        void Fail(string step, Exception error)
        {
            steps.Add(new(step, "FAILED", watch.ElapsedMilliseconds, Explain(step, error, ct)));
            logger.LogWarning("SMTP diagnosis failed at {Step}: {Diagnostic}", step, SmtpStage.Describe(error));
            foreach (var later in new[] { Connect, Encrypt, Login }.SkipWhile(name => name != step).Skip(1))
                steps.Add(new(later, "SKIPPED", 0, null));
        }

        try
        {
            using var tcp = new TcpClient();
            using var timeout = Timeout(ct);
            await tcp.ConnectAsync(options.Host, options.Port, timeout.Token);
            steps.Add(new(Connect, "OK", watch.ElapsedMilliseconds, $"已连接 {options.Host}:{options.Port}（{tcp.Client.RemoteEndPoint}）"));
        }
        catch (Exception error) when (!ct.IsCancellationRequested) { Fail(Connect, error); return steps; }

        using var client = new SmtpClient { Timeout = (int)StepTimeout.TotalMilliseconds };
        try
        {
            watch.Restart();
            using (var timeout = Timeout(ct))
                await client.ConnectAsync(options.Host, options.Port, MailKitSmtpDelivery.SocketOptionsFor(options), timeout.Token);
            var protocol = client.IsSecure ? client.SslProtocol.ToString() : "未加密";
            var mechanisms = client.AuthenticationMechanisms.Count > 0 ? string.Join("/", client.AuthenticationMechanisms) : "无";
            steps.Add(new(Encrypt, "OK", watch.ElapsedMilliseconds, $"{protocol}；服务器支持的登录方式：{mechanisms}"));
        }
        catch (Exception error) when (!ct.IsCancellationRequested) { Fail(Encrypt, error); return steps; }

        try
        {
            watch.Restart();
            using (var timeout = Timeout(ct))
                await client.AuthenticateAsync(options.Username, options.Password, timeout.Token);
            steps.Add(new(Login, "OK", watch.ElapsedMilliseconds, "登录成功（未发送邮件）"));
        }
        catch (Exception error) when (!ct.IsCancellationRequested) { Fail(Login, error); }
        finally
        {
            try
            {
                using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (client.IsConnected) await client.DisconnectAsync(true, quit.Token);
            }
            catch (Exception) { /* The check already has its answer; a failed QUIT changes nothing. */ }
        }
        return steps;
    }

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        return timeout;
    }

    internal static string Explain(string step, Exception error, CancellationToken ct) => error switch
    {
        OperationCanceledException when !ct.IsCancellationRequested => $"{StepTimeout.TotalSeconds:0} 秒内无响应：请检查网络、防火墙或代理",
        AuthenticationException => "认证失败" + SmtpTestService.AuthenticationDiagnosis(error.Message) + "：请检查账号、密码或授权码，以及邮箱是否允许 SMTP AUTH",
        SslHandshakeException => "TLS 握手失败：请检查端口、加密方式和服务器证书",
        SmtpCommandException command => $"服务器拒绝（{(int)command.StatusCode}{Code(command.Message)}）",
        SocketException socket => step == Connect
            ? $"无法连接（{socket.SocketErrorCode}）：请检查服务器地址、端口、DNS、防火墙或代理"
            : $"连接被中断（{socket.SocketErrorCode}）",
        _ when Innermost(error) is SocketException socket => $"服务器中途断开连接（{socket.SocketErrorCode}{Code(error.Message)}）",
        SmtpProtocolException or IOException => $"服务器中途断开连接{Wrap(Code(error.Message))}",
        _ => $"检测失败（{error.GetType().Name}）",
    };

    private static Exception Innermost(Exception error)
    {
        var current = error;
        while (current.InnerException is not null) current = current.InnerException;
        return current;
    }

    // Only an SMTP enhanced status code (e.g. 4.7.66) is taken from the server text, never the text itself.
    private static string Code(string message)
    {
        var code = Regex.Match(message, @"\b[245]\.\d{1,3}\.\d{1,3}\b").Value;
        return code.Length > 0 ? "，" + code : "";
    }

    private static string Wrap(string code) => code.Length > 0 ? "（" + code[1..] + "）" : "";
}
