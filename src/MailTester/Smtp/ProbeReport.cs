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
        log.Text("PORT  SECURITY               TCP    TLS    EHLO   AUTH                  TIEMPO");

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

        foreach (var line in SendCommand(options, recommended))
            log.Text("  " + line);
    }

    static string Row(AttemptResult result, CliOptions options, bool recommended)
    {
        var marker = recommended ? "   <- recomendado" : string.Empty;

        return $"{result.Port,4}  {result.Security.ToCliName(),-21}  "
               + $"{Tcp(result),-5}  {Tls(result),-5}  {Ehlo(result),-5}  {Auth(result, options),-20}  "
               + $"{result.Total.TotalMilliseconds,6:F0} ms{marker}";
    }

    static string Tcp(AttemptResult result) => result.FailedPhase switch
    {
        AttemptPhase.Dns => "-",
        AttemptPhase.TcpConnect => "FAIL",
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
            MailKit.Security.AuthenticationException => "535 credenciales",
            NotSupportedException => "no ofrece AUTH",
            _ => "FAIL",
        };
    }

    static IReadOnlyList<string> SendCommand(CliOptions options, AttemptResult recommended)
    {
        var credentials = options.ShouldAuthenticate
            ? $" --user {options.User} --pass '***'"
            : " --auth none";

        return
        [
            $"mail-tester --host {options.Host} --port {recommended.Port} --security {recommended.Security.ToCliName()}{credentials} \\",
            $"            --from {options.From?.Address ?? "tu@dominio"} --to {options.To.FirstOrDefault()?.Address ?? "destino@dominio"}",
        ];
    }
}
