using System.Text;
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
            ["C: AUTH PLAIN ***REDACTED (12 bytes)***", "S: 535 5.7.8 Error: authentication failed"],
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
}
