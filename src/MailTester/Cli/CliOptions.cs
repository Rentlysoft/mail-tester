using MimeKit;

namespace MailTester.Cli;

/// <summary>Fully validated command line input, with every default already applied.</summary>
internal sealed record CliOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 587;

    /// <summary>True when the user named a port. The probe matrix narrows itself to it.</summary>
    public bool PortSpecified { get; init; }

    public SecurityMode Security { get; init; } = SecurityMode.Auto;

    /// <summary>True when the user named a security mode. The probe matrix narrows itself to it.</summary>
    public bool SecuritySpecified { get; init; }

    public AuthMechanism Auth { get; init; } = AuthMechanism.Auto;

    public string? User { get; init; }

    public string? Password { get; init; }

    /// <summary>Null only in probe mode, which never builds a message.</summary>
    public MailboxAddress? From { get; init; }

    public IReadOnlyList<MailboxAddress> To { get; init; } = [];

    public string? Subject { get; init; }

    public string? Body { get; init; }

    public string? EhloDomain { get; init; }

    public int TimeoutSeconds { get; init; } = 30;

    public bool AllowInvalidCert { get; init; }

    public bool Probe { get; init; }

    public string? LogFile { get; init; }

    public bool ShowSecrets { get; init; }

    public bool NoColor { get; init; }

    /// <summary>
    /// Authentication is attempted only with credentials and a mechanism that is not "none".
    /// Omitting --user is the documented way to test an unauthenticated relay.
    /// </summary>
    public bool ShouldAuthenticate => Auth != AuthMechanism.None && User is not null;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}
