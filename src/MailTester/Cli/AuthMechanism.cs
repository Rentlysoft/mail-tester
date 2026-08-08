namespace MailTester.Cli;

/// <summary>Which SASL mechanism to force, or whether to negotiate / skip authentication.</summary>
internal enum AuthMechanism
{
    Auto,
    Plain,
    Login,
    CramMd5,
    Ntlm,
    None,
}

internal static class AuthMechanisms
{
    static readonly (string Name, AuthMechanism Mechanism, string? SaslName)[] Names =
    [
        ("auto", AuthMechanism.Auto, null),
        ("plain", AuthMechanism.Plain, "PLAIN"),
        ("login", AuthMechanism.Login, "LOGIN"),
        ("cram-md5", AuthMechanism.CramMd5, "CRAM-MD5"),
        ("ntlm", AuthMechanism.Ntlm, "NTLM"),
        ("none", AuthMechanism.None, null),
    ];

    public static string ValidValues { get; } = string.Join(", ", Names.Select(n => n.Name));

    public static bool TryParse(string value, out AuthMechanism mechanism)
    {
        foreach (var (name, candidate, _) in Names)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                mechanism = candidate;
                return true;
            }
        }

        mechanism = AuthMechanism.Auto;
        return false;
    }

    public static string ToCliName(this AuthMechanism mechanism) =>
        Names.First(n => n.Mechanism == mechanism).Name;

    /// <summary>The SASL name to force, or null when MailKit should negotiate or auth is skipped.</summary>
    public static string? ToSaslName(this AuthMechanism mechanism) =>
        Names.First(n => n.Mechanism == mechanism).SaslName;
}
