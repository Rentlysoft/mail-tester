using MailKit.Net.Smtp;
using MailTester.Cli;
using MailTester.Output;

namespace MailTester.Smtp;

internal static class ProbeReport
{
    public static void Render(
        ConsoleLog log,
        CliOptions options,
        IReadOnlyList<AttemptResult> results,
        AttemptResult? recommended)
    {
        log.Blank();
        log.Banner($"MATRIZ DE RESULTADOS — {options.Host}");
        log.Blank();
        log.Text("PORT   SECURITY               TCP    TLS    EHLO   AUTH                  TIEMPO");

        foreach (var result in results)
            log.Text(Row(result, options, ReferenceEquals(result, recommended)));

        log.Blank();

        if (recommended is null)
        {
            log.Line(LogLevel.Fail, "Ninguna combinación funcionó. La falla más avanzada se explica abajo.");
            return;
        }

        log.Line(LogLevel.Ok, $"Recomendado: puerto {recommended.Port} con {recommended.Security.ToCliName()}"
                              + (recommended.Secure ? " (con TLS" : " (sin cifrado")
                              + $", {recommended.Total.TotalMilliseconds:F0} ms).");
        log.Blank();
        log.Text("Para enviar un mail de prueba con esa configuración:");
        log.Text("  " + SendCommand(options, recommended));
    }

    static string Row(AttemptResult result, CliOptions options, bool recommended)
    {
        var marker = recommended ? "   <- recomendado" : string.Empty;

        // Width 5, not 4: --port accepts values up to 65535, and a 5-digit port must not shift
        // every column after it out of alignment with the header.
        return $"{result.Port,5}  {result.Security.ToCliName(),-21}  "
               + $"{Tcp(result),-5}  {Tls(result),-5}  {Ehlo(result),-5}  {Auth(result, options),-20}  "
               + $"{result.Total.TotalMilliseconds,6:F0} ms{marker}";
    }

    static string Tcp(AttemptResult result) => result.FailedPhase switch
    {
        // DNS failing means there was never an address to connect to, but the row still has to
        // read as a failed attempt rather than as nine dashes that look like nothing ran.
        AttemptPhase.Dns or AttemptPhase.TcpConnect => "FAIL",
        _ => "ok",
    };

    static string Tls(AttemptResult result)
    {
        if (result.Secure)
            return "ok";

        if (result.FailedPhase == AttemptPhase.TlsHandshake)
            return "FAIL";

        // Never attempted: either the connection died first, or no encryption was asked for.
        return "-";
    }

    static string Ehlo(AttemptResult result)
    {
        if (result.Capabilities.Count > 0)
            return "ok";

        return result.FailedPhase is AttemptPhase.Greeting or AttemptPhase.Ehlo ? "FAIL" : "-";
    }

    static string Auth(AttemptResult result, CliOptions options)
    {
        if (result.Authenticated)
            return "ok";

        if (!options.ShouldAuthenticate)
            return "-";

        if (result.FailedPhase != AttemptPhase.Authenticate)
            return "-";

        return result.Exception switch
        {
            SmtpCommandException command => $"{(int)command.StatusCode} rechazado",
            MailKit.Security.AuthenticationException authentication => AuthenticationRejection(authentication),
            NotSupportedException => "no ofrece AUTH",
            _ => "FAIL",
        };
    }

    /// <summary>
    /// MailKit throws the same AuthenticationException whether the server answered 534, 454 or
    /// 535, with the code it actually sent as a "&lt;code&gt;: " prefix on Message. Printing a
    /// fixed 535 regardless of what the server said would be inventing a fact instead of
    /// reporting one, so the code is read back out of the message, or left unstated if for some
    /// reason it is not there.
    /// </summary>
    static string AuthenticationRejection(Exception exception)
    {
        var message = exception.Message;

        return message.Length > 3 && message[3] == ':' && message[..3].All(char.IsAsciiDigit)
            ? $"{message[..3]} rechazado"
            : "credenciales rechazadas";
    }

    static string SendCommand(CliOptions options, AttemptResult recommended)
    {
        var credentials = options.ShouldAuthenticate
            ? $" --user {options.User} --pass '***'"
            : " --auth none";

        return $"mail-tester --host {options.Host} --port {recommended.Port} --security {recommended.Security.ToCliName()}{credentials}"
               + $" --from {options.From?.Address ?? "tu@dominio"} --to {options.To.FirstOrDefault()?.Address ?? "destino@dominio"}";
    }
}
