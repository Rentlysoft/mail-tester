using MailTester.Smtp;

namespace MailTester.Output;

/// <summary>
/// Hides credentials in the client side of the SMTP dialogue.
///
/// Server responses are never touched: they carry no credentials, and their status codes are
/// the entire diagnostic value of the log. MailKit ships an IAuthenticationSecretDetector, but
/// during an authentication exchange it flags the server's whole response as secret — including
/// "535 Bad credentials" — so it is unusable here.
///
/// A credential appears on the wire in exactly two places: the payload of an AUTH command, and
/// the client's answer to a 334 continuation challenge. The state machine below tracks only
/// those two moments. Because masking is tied to just-issued challenges rather than to an
/// open-ended "we are somewhere inside an exchange" flag, a server that never sends a
/// recognisable status line cannot strand the log in a masked state: it just leaves the current
/// step unresolved instead of opening a new one.
/// </summary>
internal sealed class SecretRedactor(bool showSecrets)
{
    const string AuthVerb = "AUTH";
    const string Mask = "***REDACTED***";

    ExchangeState state = ExchangeState.Idle;

    public string Client(string line)
    {
        if (IsAuthCommand(line, out var payloadStart))
        {
            state = ExchangeState.AwaitingServer;

            if (showSecrets || payloadStart >= line.Length)
                return line;

            return line[..payloadStart] + Mask;
        }

        if (state == ExchangeState.ExpectContinuation)
        {
            // LOGIN, CRAM-MD5 and NTLM answer the challenge with bare base64 that carries no
            // AUTH prefix, so the whole line is the credential.
            state = ExchangeState.AwaitingServer;
            return showSecrets ? line : Mask;
        }

        return line;
    }

    public string Server(string line)
    {
        if (state != ExchangeState.Idle && SmtpStatusCodeParser.TryParse(line, out var code))
            state = code == 334 ? ExchangeState.ExpectContinuation : ExchangeState.Idle;

        // A line with no parseable status code, or seen while Idle, changes nothing: there is
        // no challenge to open or close here.
        return line;
    }

    static bool IsAuthCommand(string line, out int payloadStart)
    {
        payloadStart = 0;

        var i = SkipSeparators(line, 0);
        if (!MatchesAt(line, i, AuthVerb))
            return false;

        i += AuthVerb.Length;

        if (i < line.Length && !IsSeparator(line[i]))
            return false; // e.g. AUTHENTICATE, which merely starts with the letters AUTH

        i = SkipSeparators(line, i);
        i = SkipNonSeparators(line, i); // the mechanism name, if any
        i = SkipSeparators(line, i);

        payloadStart = i;
        return true;
    }

    static bool MatchesAt(string line, int start, string token) =>
        start + token.Length <= line.Length &&
        string.Compare(line, start, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

    static int SkipSeparators(string line, int start)
    {
        var i = start;
        while (i < line.Length && IsSeparator(line[i]))
            i++;
        return i;
    }

    static int SkipNonSeparators(string line, int start)
    {
        var i = start;
        while (i < line.Length && !IsSeparator(line[i]))
            i++;
        return i;
    }

    static bool IsSeparator(char c) => c is ' ' or '\t';

    enum ExchangeState
    {
        /// <summary>No exchange in progress. A client line is never masked here.</summary>
        Idle,

        /// <summary>An AUTH command (or a challenge response) was just sent; waiting for the
        /// server to say whether that opens a new challenge or ends the exchange.</summary>
        AwaitingServer,

        /// <summary>The server just answered 334. The next client line is the answer to that
        /// specific challenge and gets masked whole.</summary>
        ExpectContinuation,
    }
}
