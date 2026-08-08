using System.Text;

namespace MailTester.Output;

/// <summary>
/// Hides credentials in the client side of the SMTP dialogue.
///
/// Server responses are never touched: they carry no credentials, and their status codes are
/// the entire diagnostic value of the log. MailKit ships an IAuthenticationSecretDetector, but
/// during an authentication exchange it flags the server's whole response as secret — including
/// "535 Bad credentials" — so it is unusable here.
/// </summary>
internal sealed class SecretRedactor(bool showSecrets)
{
    const string AuthVerb = "AUTH";

    /// <summary>Open from the AUTH command until the server answers with something other than 334.</summary>
    bool inAuthExchange;

    public string Client(string line)
    {
        if (IsAuthCommand(line, out var payloadStart))
        {
            inAuthExchange = true;

            if (showSecrets || payloadStart >= line.Length)
                return line;

            return line[..payloadStart] + Mask(line[payloadStart..]);
        }

        // LOGIN, CRAM-MD5 and NTLM answer the challenge with bare base64, so inside the
        // exchange the whole line is opaque.
        return inAuthExchange && !showSecrets ? Mask(line) : line;
    }

    public string Server(string line)
    {
        if (inAuthExchange && IsFinalResponse(line))
            inAuthExchange = false;

        return line;
    }

    static bool IsAuthCommand(string line, out int payloadStart)
    {
        payloadStart = 0;

        if (!line.StartsWith(AuthVerb, StringComparison.OrdinalIgnoreCase))
            return false;

        if (line.Length > AuthVerb.Length && line[AuthVerb.Length] != ' ')
            return false;

        var mechanismStart = AuthVerb.Length + 1;
        if (mechanismStart >= line.Length)
        {
            payloadStart = line.Length;
            return true;
        }

        var separator = line.IndexOf(' ', mechanismStart);
        payloadStart = separator < 0 ? line.Length : separator + 1;
        return true;
    }

    /// <summary>334 is the challenge continuation; every other status ends the exchange.</summary>
    static bool IsFinalResponse(string line) =>
        line.Length >= 3 && int.TryParse(line.AsSpan(0, 3), out var code) && code != 334;

    static string Mask(string secret) => $"***REDACTED ({Encoding.UTF8.GetByteCount(secret)} bytes)***";
}
