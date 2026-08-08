using System.Text;
using MailKit;
using MailTester.Smtp;

namespace MailTester.Output;

/// <summary>
/// Prints the SMTP dialogue, redacts the client side, and reports phase transitions.
/// MailKit may hand over several lines in one chunk, or half a line, so bytes are buffered
/// and split on newlines before anything is printed.
/// </summary>
internal sealed class SmtpProtocolLogger(
    ConsoleLog log,
    SecretRedactor redactor,
    PhaseDetector detector,
    Action<AttemptPhase> onPhase) : IProtocolLogger
{
    readonly LineBuffer clientBuffer = new();
    readonly LineBuffer serverBuffer = new();

    /// <summary>Set after a client "DATA" line, until the server's reply to it is seen.</summary>
    bool awaitingDataReply;

    /// <summary>
    /// True from a "354" reply to DATA until the client's lone "." terminator. The message body
    /// arrives through LogClient exactly like a command would, but it is user-supplied free
    /// text (e.g. --subject or --body), not protocol: a body line that happens to read
    /// "AUTH PLAIN ..." must not be redacted as a credential or attributed to the Authenticate
    /// phase.
    /// </summary>
    bool inDataMode;

    /// <summary>
    /// MailKit assigns its own detector here. It is deliberately unused: during an
    /// authentication exchange it reports the server's entire response as a secret, which
    /// would hide the status codes this tool exists to show.
    /// </summary>
    public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

    public void LogConnect(Uri uri)
    {
        // DNS and TCP are reported by the caller, which owns the socket and the timings.
    }

    public void LogClient(byte[] buffer, int offset, int count)
    {
        foreach (var line in clientBuffer.Feed(buffer, offset, count))
            EmitClient(line, reportPhase: true);
    }

    public void LogServer(byte[] buffer, int offset, int count)
    {
        foreach (var line in serverBuffer.Feed(buffer, offset, count))
            EmitServer(line, reportPhase: true);
    }

    public void Dispose()
    {
        // A server that cuts the connection mid-line still leaves evidence worth printing. The
        // fragment is incomplete, so no phase is attributed to it.
        if (clientBuffer.Flush() is { } clientRemainder)
            EmitClient(clientRemainder, reportPhase: false);

        if (serverBuffer.Flush() is { } serverRemainder)
            EmitServer(serverRemainder, reportPhase: false);
    }

    void EmitClient(string line, bool reportPhase)
    {
        if (inDataMode)
        {
            if (line == ".")
                inDataMode = false;

            log.Protocol("C: ", line);
            return;
        }

        awaitingDataReply = IsDataCommand(line);
        var phase = reportPhase ? detector.FromClient(line) : null;
        Emit("C: ", redactor.Client(line), phase);
    }

    void EmitServer(string line, bool reportPhase)
    {
        if (awaitingDataReply)
        {
            awaitingDataReply = false;
            if (SmtpStatusCode.TryParse(line, out var code) && code == 354)
                inDataMode = true;
        }

        var phase = reportPhase ? detector.FromServer(line) : null;
        Emit("S: ", redactor.Server(line), phase);
    }

    static bool IsDataCommand(string line) => string.Equals(line, "DATA", StringComparison.OrdinalIgnoreCase);

    void Emit(string prefix, string line, AttemptPhase? phase)
    {
        log.Protocol(prefix, line);

        if (phase is { } value)
            onPhase(value);
    }

    /// <summary>Accumulates raw bytes and yields complete lines, decoding one line at a time
    /// so that a multi-byte character split across chunks is never mangled.</summary>
    sealed class LineBuffer
    {
        readonly List<byte> pending = [];

        public IReadOnlyList<string> Feed(byte[] buffer, int offset, int count)
        {
            var lines = new List<string>();

            for (var i = offset; i < offset + count; i++)
            {
                if (buffer[i] == (byte)'\n')
                    lines.Add(Take());
                else
                    pending.Add(buffer[i]);
            }

            return lines;
        }

        public string? Flush() => pending.Count == 0 ? null : Take();

        string Take()
        {
            if (pending.Count > 0 && pending[^1] == (byte)'\r')
                pending.RemoveAt(pending.Count - 1);

            var line = Encoding.UTF8.GetString(pending.ToArray());
            pending.Clear();
            return line;
        }
    }
}
