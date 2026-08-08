using MailKit.Net.Smtp;
using MailTester.Cli;
using MailTester.Output;
using MailTester.Smtp;
using MimeKit;

namespace MailTester.Tests.Smtp;

public class ProbeReportTests
{
    static CliOptions Options(string? user = null) => new()
    {
        Host = "smtp.foo.com",
        User = user,
        Password = user is null ? null : "s3cr3t",
        Probe = true,
    };

    static AttemptResult Ok(int port, SecurityMode security, bool secure, bool authenticated, int totalMs) => new()
    {
        Success = true,
        Port = port,
        Security = security,
        Secure = secure,
        Authenticated = authenticated,
        LastPhase = AttemptPhase.Quit,
        Capabilities = ["AUTH=PLAIN"],
        Total = TimeSpan.FromMilliseconds(totalMs),
    };

    static AttemptResult Failed(int port, SecurityMode security, AttemptPhase phase, Exception exception, int totalMs) => new()
    {
        Success = false,
        Port = port,
        Security = security,
        LastPhase = phase,
        FailedPhase = phase,
        Exception = exception,
        Total = TimeSpan.FromMilliseconds(totalMs),
    };

    static string Render(CliOptions options, IReadOnlyList<AttemptResult> results, AttemptResult? recommended)
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);

        ProbeReport.Render(log, options, results, recommended);
        return output.ToString();
    }

    [Fact]
    public void The_table_has_a_row_per_attempt_with_its_port_and_mode()
    {
        var results = new[]
        {
            Ok(587, SecurityMode.StartTls, secure: true, authenticated: true, 298),
            Failed(465, SecurityMode.StartTls, AttemptPhase.TlsHandshake, new NotSupportedException("no STARTTLS"), 180),
        };

        var text = Render(Options("a@x.com"), results, results[0]);

        Assert.Contains("PORT", text);
        Assert.Contains("SECURITY", text);
        Assert.Contains("587", text);
        Assert.Contains("starttls", text);
        Assert.Contains("465", text);
        Assert.Contains("298", text);
        Assert.Contains("180", text);
    }

    [Fact]
    public void The_recommended_row_is_marked_and_named()
    {
        var results = new[] { Ok(587, SecurityMode.StartTls, secure: true, authenticated: true, 298) };

        var text = Render(Options("a@x.com"), results, results[0]);

        Assert.Contains("recomendado", text);
        Assert.Contains("Recomendado: puerto 587", text);
    }

    [Fact]
    public void The_report_prints_a_ready_to_run_send_command_with_the_password_masked()
    {
        var results = new[] { Ok(587, SecurityMode.StartTls, secure: true, authenticated: true, 298) };

        var text = Render(Options("a@x.com"), results, results[0]);

        Assert.Contains("mail-tester --host smtp.foo.com --port 587 --security starttls", text);
        Assert.Contains("--user a@x.com", text);
        Assert.DoesNotContain("s3cr3t", text);
    }

    [Fact]
    public void Without_credentials_the_auth_column_is_blank_rather_than_a_failure()
    {
        var results = new[] { Ok(25, SecurityMode.None, secure: false, authenticated: false, 84) };

        var text = Render(Options(), results, results[0]);

        Assert.DoesNotContain("FAIL", text);
    }

    [Fact]
    public void A_rejected_authentication_shows_its_status_code_in_the_auth_column()
    {
        var exception = new SmtpCommandException(
            SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationRequired,
            "530 5.7.0 Must issue a STARTTLS command first");
        var results = new[] { Failed(25, SecurityMode.None, AttemptPhase.Authenticate, exception, 84) };

        var text = Render(Options("a@x.com"), results, recommended: null);

        Assert.Contains("530", text);
    }

    [Fact]
    public void When_nothing_worked_the_report_says_so_instead_of_inventing_a_recommendation()
    {
        var results = new[]
        {
            Failed(25, SecurityMode.None, AttemptPhase.TcpConnect, new Exception("refused"), 12),
        };

        var text = Render(Options(), results, recommended: null);

        Assert.Contains("Ninguna combinación funcionó", text);
        Assert.DoesNotContain("Recomendado:", text);
    }

    [Fact]
    public void A_failure_before_tls_leaves_the_later_columns_empty_rather_than_claiming_failure()
    {
        var results = new[]
        {
            Failed(2525, SecurityMode.StartTls, AttemptPhase.TcpConnect, new Exception("refused"), 12),
        };

        var text = Render(Options(), results, recommended: null);
        var row = text.ReplaceLineEndings("\n").Split('\n').Single(l => l.Contains("2525"));

        // TCP failed, so nothing after it was attempted and nothing after it is reported.
        Assert.Equal(1, row.Split("FAIL").Length - 1);
    }
}
