using MailTester.Cli;

namespace MailTester.Smtp;

internal sealed record ProbeCombination(int Port, SecurityMode Security);

internal static class ProbeMatrix
{
    static readonly int[] DefaultPorts = [25, 587, 465, 2525];

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
    /// The failure that got furthest is the one whose explanation is most useful. The ranking is
    /// approximate for implicit TLS, where the handshake precedes the greeting.
    /// </summary>
    public static AttemptResult MostAdvancedFailure(IReadOnlyList<AttemptResult> results) =>
        results
            .OrderByDescending(result => Rank(result.FailedPhase ?? result.LastPhase))
            .ThenBy(result => result.Total)
            .First();

    static int Rank(AttemptPhase phase) => phase switch
    {
        AttemptPhase.Dns => 0,
        AttemptPhase.TcpConnect => 1,
        AttemptPhase.Greeting => 2,
        AttemptPhase.TlsHandshake => 3,
        AttemptPhase.Ehlo => 4,
        AttemptPhase.Authenticate => 5,
        AttemptPhase.Send => 6,
        AttemptPhase.Quit => 7,
        _ => 0,
    };
}
