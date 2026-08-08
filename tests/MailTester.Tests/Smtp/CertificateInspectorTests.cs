using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailTester.Output;
using MailTester.Smtp;

namespace MailTester.Tests.Smtp;

public class CertificateInspectorTests
{
    static X509Certificate2 SelfSigned(string commonName, string[] dnsNames, int daysUntilExpiry = 30)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
            san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.Now;
        var notBefore = now.AddDays(Math.Min(-1, daysUntilExpiry - 1));
        return request.CreateSelfSigned(notBefore, now.AddDays(daysUntilExpiry));
    }

    static (CertificateInspector Inspector, StringWriter Output) Build(string expectedHost, bool allowInvalid)
    {
        var output = new StringWriter();
        var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);
        return (new CertificateInspector(log, expectedHost, allowInvalid), output);
    }

    [Fact]
    public void A_valid_certificate_is_accepted_and_fully_reported()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: false);
        using var certificate = SelfSigned("smtp.foo.com", ["smtp.foo.com", "mail.foo.com"]);

        var accepted = inspector.Validate(this, certificate, null, SslPolicyErrors.None);
        var text = output.ToString();

        Assert.True(accepted);
        Assert.Contains("CN=smtp.foo.com", text);
        Assert.Contains("smtp.foo.com, mail.foo.com", text);
        Assert.Contains("días restantes", text);
        Assert.DoesNotContain("WARN", text);
    }

    [Fact]
    public void The_inspected_certificate_and_errors_are_exposed_for_the_result()
    {
        var (inspector, _) = Build("smtp.foo.com", allowInvalid: false);
        using var certificate = SelfSigned("smtp.foo.com", ["smtp.foo.com"]);

        inspector.Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.NotNull(inspector.ServerCertificate);
        Assert.Equal("CN=smtp.foo.com", inspector.ServerCertificate!.Subject);
        Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, inspector.Errors);
    }

    [Fact]
    public void Validation_errors_are_listed_and_the_certificate_is_rejected()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: false);
        using var certificate = SelfSigned("otro.host", ["otro.host"]);

        var accepted = inspector.Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch);
        var text = output.ToString();

        Assert.False(accepted);
        Assert.Contains("RemoteCertificateNameMismatch", text);
    }

    [Fact]
    public void Allow_invalid_cert_accepts_the_certificate_but_says_so_loudly()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: true);
        using var certificate = SelfSigned("otro.host", ["otro.host"]);

        var accepted = inspector.Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch);
        var text = output.ToString();

        Assert.True(accepted);
        Assert.Contains("WARN", text);
        Assert.Contains("--allow-invalid-cert", text);
    }

    [Fact]
    public void A_host_not_covered_by_the_san_is_called_out()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: false);
        using var certificate = SelfSigned("otro.host", ["otro.host"]);

        inspector.Validate(this, certificate, null, SslPolicyErrors.None);

        Assert.Contains("el SAN no cubre smtp.foo.com", output.ToString());
    }

    [Theory]
    [InlineData("smtp.foo.com", "*.foo.com", true)]
    [InlineData("a.b.foo.com", "*.foo.com", false)]
    [InlineData("foo.com", "*.foo.com", false)]
    [InlineData("SMTP.FOO.COM", "smtp.foo.com", true)]
    public void San_matching_follows_single_label_wildcard_rules(string host, string san, bool covered)
    {
        var (inspector, output) = Build(host, allowInvalid: false);
        using var certificate = SelfSigned("whatever", [san]);

        inspector.Validate(this, certificate, null, SslPolicyErrors.None);

        var text = output.ToString();
        Assert.Equal(covered, !text.Contains("el SAN no cubre", StringComparison.Ordinal));
    }

    [Fact]
    public void An_expired_certificate_is_reported_as_expired()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: true);
        using var certificate = SelfSigned("smtp.foo.com", ["smtp.foo.com"], daysUntilExpiry: -5);

        inspector.Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.Contains("expirado", output.ToString());
    }

    [Fact]
    public void Chain_status_is_reported_alongside_validation_errors()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: false);
        using var certificate = SelfSigned("smtp.foo.com", ["smtp.foo.com"]);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate);

        inspector.Validate(this, certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.NotEmpty(chain.ChainStatus);
        Assert.Contains("cadena", output.ToString());
    }

    [Fact]
    public void A_clean_result_prints_no_chain_status_even_when_a_chain_is_supplied()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: false);
        using var certificate = SelfSigned("smtp.foo.com", ["smtp.foo.com"]);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate);

        inspector.Validate(this, certificate, chain, SslPolicyErrors.None);

        var text = output.ToString();
        Assert.DoesNotContain("cadena", text);
        Assert.DoesNotContain("WARN", text);
    }

    [Fact]
    public void A_missing_certificate_is_reported_rather_than_crashing()
    {
        var (inspector, output) = Build("smtp.foo.com", allowInvalid: false);

        var accepted = inspector.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable);

        Assert.False(accepted);
        Assert.Contains("no presentó certificado", output.ToString());
        Assert.Null(inspector.ServerCertificate);
    }
}
