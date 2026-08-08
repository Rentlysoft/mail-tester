using MailKit.Net.Smtp;
using MailKit.Security;
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

    [Fact]
    public void A_tls_failure_after_a_successful_ehlo_still_reports_ehlo_ok()
    {
        var results = new[]
        {
            Failed(587, SecurityMode.StartTls, AttemptPhase.TlsHandshake, new SslHandshakeException("cert inválido"), 120) with
            {
                // The EHLO capabilities MailKit parsed before attempting the handshake, exactly
                // as a real attempt reports them on this failure path.
                Capabilities = ["AUTH=PLAIN LOGIN", "STARTTLS"],
            },
        };

        var text = Render(Options(), results, recommended: null);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var header = lines.Single(l => l.StartsWith("PORT", StringComparison.Ordinal));
        var row = lines.Single(l => l.Contains("587"));

        var tlsColumn = header.IndexOf("TLS", StringComparison.Ordinal);
        var ehloColumn = header.IndexOf("EHLO", StringComparison.Ordinal);

        // The server answered EHLO -- and advertised STARTTLS -- before the handshake itself
        // failed on the certificate. That EHLO succeeded, and its column must say so instead of
        // reading as a failure or as a phase the attempt never reached.
        Assert.Equal("FAIL", row.Substring(tlsColumn, 5).Trim());
        Assert.Equal("ok", row.Substring(ehloColumn, 5).Trim());
    }

    [Theory]
    [InlineData(25)]
    [InlineData(2525)]
    [InlineData(65535)]
    public void The_security_column_stays_aligned_regardless_of_how_many_digits_the_port_has(int port)
    {
        var results = new[] { Ok(port, SecurityMode.StartTlsIfAvailable, secure: true, authenticated: true, 1) };

        var text = Render(Options(), results, results[0]);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var header = lines.Single(l => l.StartsWith("PORT", StringComparison.Ordinal));
        // The "Recomendado:" line below the table names the same port and security mode, so the
        // row is identified by starting with the port digits rather than merely containing them.
        var row = lines.Single(l => l.TrimStart().StartsWith(port.ToString(), StringComparison.Ordinal));

        Assert.Equal(
            header.IndexOf("SECURITY", StringComparison.Ordinal),
            row.IndexOf("starttls-if-available", StringComparison.Ordinal));
    }
}
