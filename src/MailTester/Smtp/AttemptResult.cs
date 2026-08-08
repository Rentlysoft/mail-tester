using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MailTester.Cli;

namespace MailTester.Smtp;

/// <summary>Everything one attempt learned, whether it succeeded or not.</summary>
internal sealed record AttemptResult
{
    public required bool Success { get; init; }

    public required int Port { get; init; }

    public required SecurityMode Security { get; init; }

    public required AttemptPhase LastPhase { get; init; }

    public AttemptPhase? FailedPhase { get; init; }

    public Exception? Exception { get; init; }

    public IReadOnlyList<IPAddress> ResolvedAddresses { get; init; } = [];

    public IPAddress? ConnectedAddress { get; init; }

    public string? LocalEndPoint { get; init; }

    public bool Secure { get; init; }

    public SslProtocols? TlsProtocol { get; init; }

    public string? CipherSuite { get; init; }

    public X509Certificate2? ServerCertificate { get; init; }

    public SslPolicyErrors CertificateErrors { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public IReadOnlyList<string> AuthMechanismsOffered { get; init; } = [];

    public string? AuthMechanismUsed { get; init; }

    public bool Authenticated { get; init; }

    public bool MessageSent { get; init; }

    /// <summary>The server's answer to DATA, which usually carries the queue id.</summary>
    public string? ServerResponse { get; init; }

    public string? MessageId { get; init; }

    public TimeSpan Total { get; init; }

    public IReadOnlyDictionary<AttemptPhase, TimeSpan> PhaseTimings { get; init; } =
        new Dictionary<AttemptPhase, TimeSpan>();
}
