using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailTester.Output;

namespace MailTester.Smtp;

/// <summary>
/// Reports the server certificate and decides whether to accept it. The certificate is always
/// reported, valid or not: "which certificate did I actually get" is usually the question.
/// </summary>
internal sealed class CertificateInspector(ConsoleLog log, string expectedHost, bool allowInvalid)
{
    public X509Certificate2? ServerCertificate { get; private set; }

    public SslPolicyErrors Errors { get; private set; } = SslPolicyErrors.None;

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        Errors = errors;

        if (certificate is null)
        {
            log.Line(LogLevel.Fail, "El servidor no presentó certificado.");
            return false;
        }

        var cert = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        ServerCertificate = cert;

        Report(cert, chain, errors);

        if (errors == SslPolicyErrors.None)
            return true;

        if (!allowInvalid)
            return false;

        log.Line(LogLevel.Warn, "Certificado inválido aceptado por --allow-invalid-cert. La conexión sigue, pero no está verificada.");
        return true;
    }

    void Report(X509Certificate2 cert, X509Chain? chain, SslPolicyErrors errors)
    {
        log.Line(LogLevel.Cert, $"{cert.Subject} · issuer={cert.Issuer}");

        var dnsNames = DnsNames(cert);
        var sanText = dnsNames.Count == 0
            ? "sin SAN"
            : $"SAN: {string.Join(", ", dnsNames)}";
        var coverage = dnsNames.Any(name => Covers(name, expectedHost))
            ? string.Empty
            : $" · el SAN no cubre {expectedHost}";

        log.Line(LogLevel.Cert, $"{Validity(cert)} · {sanText}{coverage}");
        log.Line(LogLevel.Cert, $"thumbprint={cert.Thumbprint} · firma={cert.SignatureAlgorithm.FriendlyName}");

        if (errors != SslPolicyErrors.None)
        {
            log.Line(LogLevel.Warn, $"La validación del certificado falló: {errors}");

            foreach (var flag in Enum.GetValues<SslPolicyErrors>())
            {
                if (flag != SslPolicyErrors.None && errors.HasFlag(flag))
                    log.Line(LogLevel.Warn, $"  {flag}: {Explain(flag)}");
            }
        }

        foreach (var status in chain?.ChainStatus ?? [])
            log.Line(LogLevel.Warn, $"  cadena {status.Status}: {status.StatusInformation?.Trim()}");
    }

    static string Validity(X509Certificate2 cert)
    {
        var window = $"válido {cert.NotBefore:yyyy-MM-dd} .. {cert.NotAfter:yyyy-MM-dd}";
        var days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);

        return days < 0
            ? $"{window} (expirado hace {-days} días)"
            : $"{window} ({days} días restantes)";
    }

    static IReadOnlyList<string> DnsNames(X509Certificate2 cert) =>
    [
        .. cert.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(extension => extension.EnumerateDnsNames()),
    ];

    /// <summary>Wildcards cover exactly one label, the way a real TLS client matches them.</summary>
    static bool Covers(string sanName, string host)
    {
        if (string.Equals(sanName, host, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!sanName.StartsWith("*.", StringComparison.Ordinal))
            return false;

        var suffix = sanName[1..];
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var label = host[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal);
    }

    static string Explain(SslPolicyErrors error) => error switch
    {
        SslPolicyErrors.RemoteCertificateNotAvailable => "el servidor no envió certificado",
        SslPolicyErrors.RemoteCertificateNameMismatch => "el nombre del certificado no coincide con el host al que te conectaste",
        SslPolicyErrors.RemoteCertificateChainErrors => "la cadena no valida: puede estar expirado, autofirmado, o faltar el intermedio",
        _ => "error de validación no clasificado",
    };
}
