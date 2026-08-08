namespace MailTester.Smtp;

/// <summary>
/// The stage of an attempt, used to attribute a failure. This is a label, not an ordering:
/// with implicit TLS the handshake happens before the greeting, and with STARTTLS after the EHLO.
/// </summary>
internal enum AttemptPhase
{
    Dns,
    TcpConnect,
    TlsHandshake,
    Greeting,
    Ehlo,
    Authenticate,
    Send,
    Quit,
}
