using MailTester.Cli;
using MimeKit;

namespace MailTester.Messages;

/// <summary>
/// Facts about the live connection that belong in the body, so that a message which arrives
/// can be tied back to the exact configuration that sent it.
/// </summary>
internal sealed record MessageContext(DateTimeOffset Timestamp, string Server, string Auth, string Origin);

internal static class TestMessageFactory
{
    public static MimeMessage Create(CliOptions options, MessageContext context)
    {
        if (options.From is null)
            throw new InvalidOperationException("Se pidió armar un mensaje sin remitente; el parseo de argumentos exige --from en modo send.");

        if (options.To.Count == 0)
            throw new InvalidOperationException("Se pidió armar un mensaje sin destinatarios; el parseo de argumentos exige --to en modo send.");

        var message = new MimeMessage { Date = context.Timestamp };
        message.From.Add(options.From);

        foreach (var recipient in options.To)
            message.To.Add(recipient);

        message.Subject = options.Subject ?? $"mail-tester {context.Timestamp.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        message.Headers.Add("X-Mailer", "mail-tester");
        message.Body = new TextPart("plain") { Text = options.Body ?? DefaultBody(context) };

        return message;
    }

    static string DefaultBody(MessageContext context) =>
        $"""
        Mail de prueba enviado por mail-tester.

        Timestamp : {context.Timestamp.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}
        Servidor  : {context.Server}
        Auth      : {context.Auth}
        Origen    : {context.Origin}

        Si recibiste este mail, la configuración SMTP funciona.
        """;
}
