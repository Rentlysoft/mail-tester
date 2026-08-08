namespace MailTester.Smtp;

/// <summary>
/// Reads the phase out of the SMTP dialogue. MailKit's Connect does the greeting, the EHLO and
/// STARTTLS in one call, so the only way to attribute a failure to one of them is to watch the
/// wire. Implicit TLS produces no STARTTLS command, so the caller sets that phase itself.
/// </summary>
internal sealed class PhaseDetector
{
    bool greetingSeen;

    public AttemptPhase? FromClient(string line)
    {
        if (StartsWith(line, "EHLO") || StartsWith(line, "HELO"))
            return AttemptPhase.Ehlo;

        if (StartsWith(line, "STARTTLS"))
            return AttemptPhase.TlsHandshake;

        if (StartsWith(line, "AUTH"))
            return AttemptPhase.Authenticate;

        if (StartsWith(line, "MAIL FROM"))
            return AttemptPhase.Send;

        if (StartsWith(line, "QUIT"))
            return AttemptPhase.Quit;

        return null;
    }

    public AttemptPhase? FromServer(string line)
    {
        // STARTTLS is also answered with 220, so only the first one is the greeting.
        if (greetingSeen || !StartsWith(line, "220"))
            return null;

        greetingSeen = true;
        return AttemptPhase.Greeting;
    }

    static bool StartsWith(string line, string token) =>
        line.StartsWith(token, StringComparison.OrdinalIgnoreCase);
}
