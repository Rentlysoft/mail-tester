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

        log.Line(LogLevel.Conf, $"{HostAndSecurity(options)} auth={options.Auth.ToCliName()} {Credentials(options)}");

        if (!options.Probe)
            log.Line(LogLevel.Conf, $"from={options.From?.Address} to={string.Join(", ", options.To.Select(t => t.Address))}");

        log.Line(LogLevel.Conf, $"timeout={options.TimeoutSeconds}s ehlo-domain={options.EhloDomain ?? Environment.MachineName} allow-invalid-cert={options.AllowInvalidCert}");
    }

    /// <summary>
    /// Outside --probe, host:port and security are always the configuration the run is about to
    /// use, named or defaulted. Inside --probe, they are only that certain when the user named
    /// them explicitly: ProbeMatrix sweeps whichever of the two was left unnamed across several
    /// ports or security modes, so stating the default port or the default security mode there
    /// would claim a single fixed value for something the run is about to try many ways.
    /// </summary>
    static string HostAndSecurity(CliOptions options)
    {
        if (!options.Probe || (options.PortSpecified && options.SecuritySpecified))
            return $"host={options.Host}:{options.Port} security={options.Security.ToCliName()}";

        var port = options.PortSpecified ? options.Port.ToString() : "barre";
        var security = options.SecuritySpecified ? options.Security.ToCliName() : "barre";
        return $"host={options.Host} port={port} security={security}";
    }

    /// <summary>Never the password itself, and not even its length: a log gets pasted into a
    /// ticket, and the character count is a fact about the credential that buys the reader
    /// nothing. The protocol log redacts the same credential in the wire dialogue.</summary>
    static string Credentials(CliOptions options) =>
        options.ShouldAuthenticate
            ? $"user={options.User} pass=***"
            : "sin autenticación";

    static string MailKitVersion() =>
        typeof(SmtpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SmtpClient).Assembly.GetName().Version?.ToString()
        ?? "desconocida";
}
