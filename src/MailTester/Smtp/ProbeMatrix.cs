using MailTester.Cli;

namespace MailTester.Smtp;

internal sealed record ProbeCombination(int Port, SecurityMode Security);

internal static class ProbeMatrix
{
    /// <summary>
    /// Curated per port rather than a full cartesian product: sweeping every mode against every
    /// port would spend timeouts on combinations nobody deploys. The order within each port is
    /// most likely first.
    /// </summary>
    static readonly (int Port, SecurityMode[] Modes)[] Curated =
    [
        (25, [SecurityMode.None, SecurityMode.StartTls]),
        (587, [SecurityMode.StartTls, SecurityMode.None, SecurityMode.Ssl]),
        (465, [SecurityMode.Ssl, SecurityMode.StartTls]),
        (2525, [SecurityMode.StartTls, SecurityMode.None]),
    ];

    static readonly int[] DefaultPorts = [.. Curated.Select(entry => entry.Port)];

    static readonly SecurityMode[] ModesForANamedPort = [SecurityMode.StartTls, SecurityMode.Ssl, SecurityMode.None];

    public static IReadOnlyList<ProbeCombination> Build(CliOptions options)
    {
        if (options.PortSpecified && options.SecuritySpecified)
            return [new ProbeCombination(options.Port, options.Security)];

        if (options.PortSpecified)
            return [.. ModesForANamedPort.Select(mode => new ProbeCombination(options.Port, mode))];

        if (options.SecuritySpecified)
            return [.. DefaultPorts.Select(port => new ProbeCombination(port, options.Security))];

        return [.. Curated.SelectMany(entry => entry.Modes.Select(mode => new ProbeCombination(entry.Port, mode)))];
    }

    /// <summary>
    /// Encryption first, then speed. When credentials were supplied, a combination that connected
    /// but did not authenticate is not a working configuration.
    /// </summary>
    public static AttemptResult? Recommend(IReadOnlyList<AttemptResult> results, bool credentialsGiven) =>
        results
            .Where(result => result.Success && (!credentialsGiven || result.Authenticated))
            .OrderByDescending(result => result.Secure)
            .ThenBy(result => result.Total)
            .FirstOrDefault();

    /// <summary>
    /// The failure that got furthest is the one whose explanation is most useful. For implicit TLS
    /// (Ssl), the handshake precedes the greeting, so a failure at greeting with completed
    /// handshake is more advanced than a plaintext failure at the same phase.
    /// </summary>
    public static AttemptResult? MostAdvancedFailure(IReadOnlyList<AttemptResult> results) =>
        results
            .Where(result => !result.Success)
            .OrderByDescending(result => Rank(result.Security, result.FailedPhase ?? result.LastPhase))
            .ThenBy(result => result.Total)
            .FirstOrDefault();

    static int Rank(SecurityMode security, AttemptPhase phase) => phase switch
    {
        AttemptPhase.Dns => 0,
        AttemptPhase.TcpConnect => 1,
        AttemptPhase.TlsHandshake => security == SecurityMode.Ssl ? 2 : 3,
        AttemptPhase.Greeting => security == SecurityMode.Ssl ? 3 : 2,
        AttemptPhase.Ehlo => 4,
        AttemptPhase.Authenticate => 5,
        AttemptPhase.Send => 6,
        AttemptPhase.Quit => 7,
        _ => 0,
    };
}
