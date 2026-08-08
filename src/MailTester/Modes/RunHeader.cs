using System.Reflection;
using MailKit.Net.Smtp;
using MailTester.Cli;
using MailTester.Output;

namespace MailTester.Modes;

internal static class RunHeader
{
    public static void Render(ConsoleLog log, CliOptions options, string? logFileWarning)
    {
        log.Line(LogLevel.Info, $"mail-tester · .NET {Environment.Version} · MailKit {MailKitVersion()}");

        if (logFileWarning is not null)
            log.Line(LogLevel.Warn, logFileWarning);

        log.Line(LogLevel.Conf, $"host={options.Host}:{options.Port} security={options.Security.ToCliName()} auth={options.Auth.ToCliName()} {Credentials(options)}");

        if (!options.Probe)
            log.Line(LogLevel.Conf, $"from={options.From?.Address} to={string.Join(", ", options.To.Select(t => t.Address))}");

        log.Line(LogLevel.Conf, $"timeout={options.TimeoutSeconds}s ehlo-domain={options.EhloDomain ?? Environment.MachineName} allow-invalid-cert={options.AllowInvalidCert}");
    }

    /// <summary>Length only, never the password itself. The protocol log redacts credentials
    /// in the wire dialogue; printing the password here would defeat that.</summary>
    static string Credentials(CliOptions options) =>
        options.ShouldAuthenticate
            ? $"user={options.User} pass=*** ({options.Password?.Length ?? 0} chars)"
            : "sin autenticación";

    static string MailKitVersion() =>
        typeof(SmtpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SmtpClient).Assembly.GetName().Version?.ToString()
        ?? "desconocida";
}
