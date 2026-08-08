using System.Text;
using MailKit;
using MailTester.Output;
using MailTester.Smtp;

namespace MailTester.Tests.Output;

public class SmtpProtocolLoggerTests
{
    sealed class Harness : IDisposable
    {
        public StringWriter Output { get; } = new();

        public List<AttemptPhase> Phases { get; } = [];

        public ConsoleLog Log { get; }

        public SmtpProtocolLogger Logger { get; }

        public Harness(bool showSecrets = false)
        {
            Log = new ConsoleLog(Output, null, new NullColorizer(), () => TimeSpan.Zero);
            Logger = new SmtpProtocolLogger(Log, new SecretRedactor(showSecrets), new PhaseDetector(), Phases.Add);
        }

        public void Client(string text) => Feed(Logger.LogClient, text);

        public void Server(string text) => Feed(Logger.LogServer, text);

        public string[] Lines() =>
            Output.ToString().TrimEnd().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        static void Feed(Action<byte[], int, int> sink, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            sink(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            Logger.Dispose();
            Log.Dispose();
        }
    }

    [Fact]
    public void Every_line_of_a_multi_line_chunk_gets_its_own_prefix()
    {
        using var harness = new Harness();

        harness.Server("250-fake.local\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n");

        Assert.Equal(
            ["S: 250-fake.local", "S: 250-AUTH PLAIN LOGIN", "S: 250 8BITMIME"],
            harness.Lines());
    }

    [Fact]
    public void A_line_split_across_chunks_is_emitted_once_and_complete()
    {
        using var harness = new Harness();

        harness.Server("220 fake.local ES");
        Assert.Empty(harness.Lines());

        harness.Server("MTP Postfix\r\n");

        Assert.Equal(["S: 220 fake.local ESMTP Postfix"], harness.Lines());
    }

    [Fact]
    public void Client_lines_are_redacted_and_server_lines_are_not()
    {
        using var harness = new Harness();

        harness.Client("AUTH PLAIN AGJvYgBwYXNz\r\n");
        harness.Server("535 5.7.8 Error: authentication failed\r\n");

        Assert.Equal(
            ["C: AUTH PLAIN ***REDACTED***", "S: 535 5.7.8 Error: authentication failed"],
            harness.Lines());
    }

    [Fact]
    public void Show_secrets_prints_the_credential_verbatim()
    {
        using var harness = new Harness(showSecrets: true);

        harness.Client("AUTH PLAIN AGJvYgBwYXNz\r\n");

        Assert.Equal(["C: AUTH PLAIN AGJvYgBwYXNz"], harness.Lines());
    }

    [Fact]
    public void Phases_are_reported_in_the_order_they_happen()
    {
        using var harness = new Harness();

        harness.Server("220 fake.local ESMTP\r\n");
        harness.Client("EHLO desktop-abc\r\n");
        harness.Server("250-STARTTLS\r\n250 AUTH PLAIN\r\n");
        harness.Client("STARTTLS\r\n");
        harness.Server("220 2.0.0 Ready to start TLS\r\n");
        harness.Client("EHLO desktop-abc\r\n");
        harness.Client("AUTH PLAIN AGJvYgBwYXNz\r\n");
        harness.Server("235 2.7.0 Authentication successful\r\n");
        harness.Client("MAIL FROM:<a@x.com>\r\n");
        harness.Client("QUIT\r\n");

        Assert.Equal(
            [
                AttemptPhase.Greeting,
                AttemptPhase.Ehlo,
                AttemptPhase.TlsHandshake,
                AttemptPhase.Ehlo,
                AttemptPhase.Authenticate,
                AttemptPhase.Send,
                AttemptPhase.Quit,
            ],
            harness.Phases);
    }

    [Fact]
    public void The_second_220_is_not_mistaken_for_the_greeting()
    {
        using var harness = new Harness();

        harness.Server("220 fake.local ESMTP\r\n");
        harness.Client("EHLO desktop-abc\r\n");
        harness.Client("STARTTLS\r\n");
        harness.Server("220 2.0.0 Ready to start TLS\r\n");

        Assert.Single(harness.Phases, AttemptPhase.Greeting);
    }

    [Fact]
    public void A_truncated_final_line_is_still_shown_when_the_logger_is_disposed()
    {
        var harness = new Harness();

        harness.Server("220 fake.local ESM");
        harness.Logger.Dispose();

        Assert.Equal(["S: 220 fake.local ESM"], harness.Lines());
        harness.Log.Dispose();
    }

    [Fact]
    public void Disposing_mid_credential_still_redacts_the_flushed_remainder()
    {
        var harness = new Harness();

        harness.Client("AUTH LOGIN\r\n");
        harness.Server("334 VXNlcm5hbWU6\r\n");
        harness.Client("Ym9iQGZha2UubG9jYWw="); // no trailing CRLF: still buffered at dispose time
        harness.Logger.Dispose();

        Assert.Equal(
            ["C: AUTH LOGIN", "S: 334 VXNlcm5hbWU6", "C: ***REDACTED***"],
            harness.Lines());
        harness.Log.Dispose();
    }

    [Fact]
    public void A_credential_split_across_client_chunks_is_redacted_once_reassembled()
    {
        using var harness = new Harness();

        harness.Client("AUTH PLAIN AGJv");
        Assert.Empty(harness.Lines());

        harness.Client("YgBwYXNz\r\n");

        Assert.Equal(["C: AUTH PLAIN ***REDACTED***"], harness.Lines());
    }

    sealed class ThrowingSecretDetector : IAuthenticationSecretDetector
    {
        public IList<AuthenticationSecret> DetectSecrets(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException(
                "SmtpProtocolLogger must never consult MailKit's own secret detector.");
    }

    [Fact]
    public void MailKits_own_secret_detector_is_never_consulted()
    {
        using var harness = new Harness();
        harness.Logger.AuthenticationSecretDetector = new ThrowingSecretDetector();

        harness.Client("AUTH PLAIN AGJvYgBwYXNz\r\n");
        harness.Server("535 5.7.8 Bad credentials\r\n");

        Assert.Equal(
            ["C: AUTH PLAIN ***REDACTED***", "S: 535 5.7.8 Bad credentials"],
            harness.Lines());
    }

    [Fact]
    public void Ntlm_masks_both_challenge_lines_and_leaves_what_follows_visible()
    {
        using var harness = new Harness();

        harness.Client("AUTH NTLM TlRMTVNTUAABAAAAB4IIogAAAAAAAAAAAAAAAAAAAAA=\r\n");
        harness.Server("334 TlRMTVNTUAACAAAA\r\n");
        harness.Client("TlRMTVNTUAADAAAAGAAYAEgAAAAYABgAYAAAAAAAAAB4AAAA\r\n");
        harness.Server("235 2.7.0 Authentication successful\r\n");
        harness.Client("MAIL FROM:<a@x.com>\r\n");

        Assert.Equal(
            [
                "C: AUTH NTLM ***REDACTED***",
                "S: 334 TlRMTVNTUAACAAAA",
                "C: ***REDACTED***",
                "S: 235 2.7.0 Authentication successful",
                "C: MAIL FROM:<a@x.com>",
            ],
            harness.Lines());
    }

    [Fact]
    public void A_non_status_server_line_during_an_exchange_does_not_strand_the_log_in_a_masked_state()
    {
        using var harness = new Harness();

        harness.Client("AUTH LOGIN\r\n");
        harness.Server("<html>this is not an SMTP reply</html>\r\n");
        harness.Server("235 2.7.0 Authentication successful\r\n");
        harness.Client("MAIL FROM:<a@x.com>\r\n");

        Assert.Equal(
            [
                "C: AUTH LOGIN",
                "S: <html>this is not an SMTP reply</html>",
                "S: 235 2.7.0 Authentication successful",
                "C: MAIL FROM:<a@x.com>",
            ],
            harness.Lines());
    }

    [Fact]
    public void Verb_prefixes_without_a_delimiter_are_not_mistaken_for_a_command()
    {
        using var harness = new Harness();

        harness.Client("EHLO desktop-abc\r\n");
        harness.Client("AUTHORIZATION\r\n");
        harness.Client("QUITxyz+/A=\r\n");
        harness.Client("MAIL FROMage\r\n");

        Assert.Equal([AttemptPhase.Ehlo], harness.Phases);
    }

    [Fact]
    public void A_body_line_that_looks_like_an_auth_command_is_printed_verbatim_during_data_mode()
    {
        using var harness = new Harness();

        harness.Client("MAIL FROM:<a@x.com>\r\n");
        harness.Client("RCPT TO:<b@y.com>\r\n");
        harness.Client("DATA\r\n");
        harness.Server("354 Start mail input; end with <CRLF>.<CRLF>\r\n");
        harness.Client("Subject: test\r\n");
        harness.Client("AUTH PLAIN AGJvYgBwYXNz\r\n");
        harness.Client(".\r\n");
        harness.Server("250 2.0.0 Ok: queued as ABC123\r\n");
        harness.Client("QUIT\r\n");

        Assert.Equal(
            [
                "C: MAIL FROM:<a@x.com>",
                "C: RCPT TO:<b@y.com>",
                "C: DATA",
                "S: 354 Start mail input; end with <CRLF>.<CRLF>",
                "C: Subject: test",
                "C: AUTH PLAIN AGJvYgBwYXNz",
                "C: .",
                "S: 250 2.0.0 Ok: queued as ABC123",
                "C: QUIT",
            ],
            harness.Lines());

        Assert.Equal([AttemptPhase.Send, AttemptPhase.Quit], harness.Phases);
    }
}
