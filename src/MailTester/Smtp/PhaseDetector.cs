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
        if (MatchesVerb(line, "EHLO") || MatchesVerb(line, "HELO"))
            return AttemptPhase.Ehlo;

        if (MatchesVerb(line, "STARTTLS"))
            return AttemptPhase.TlsHandshake;

        if (MatchesVerb(line, "AUTH"))
            return AttemptPhase.Authenticate;

        if (MatchesVerb(line, "MAIL FROM"))
            return AttemptPhase.Send;

        if (MatchesVerb(line, "QUIT"))
            return AttemptPhase.Quit;

        return null;
    }

    public AttemptPhase? FromServer(string line)
    {
        // STARTTLS is also answered with 220, so only the first one is the greeting.
        if (greetingSeen || !SmtpStatusCodeParser.TryParse(line, out var code) || code != 220)
            return null;

        greetingSeen = true;
        return AttemptPhase.Greeting;
    }

    /// <summary>
    /// A bare prefix match would mistake "authorization" for AUTH, "QUITxyz" for QUIT, or
    /// "MAIL FROMage" for MAIL FROM. The character right after the verb must end the line, or
    /// be a space, or be a colon — the last one is for "MAIL FROM:&lt;addr&gt;", which has no
    /// space before the address.
    /// </summary>
    static bool MatchesVerb(string line, string verb)
    {
        if (!line.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            return false;

        if (line.Length == verb.Length)
            return true;

        var next = line[verb.Length];
        return next == ' ' || next == ':';
    }
}
