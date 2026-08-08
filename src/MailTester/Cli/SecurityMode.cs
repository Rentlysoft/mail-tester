using MailKit.Security;

namespace MailTester.Cli;

/// <summary>TLS negotiation strategy, exposed one-to-one with MailKit's own options.</summary>
internal enum SecurityMode
{
    None,
    Auto,
    StartTlsIfAvailable,
    StartTls,
    Ssl,
}

internal static class SecurityModes
{
    // Insertion order is the order shown in --help and in error messages: least to most secure.
    static readonly (string Name, SecurityMode Mode)[] Names =
    [
        ("none", SecurityMode.None),
        ("auto", SecurityMode.Auto),
        ("starttls-if-available", SecurityMode.StartTlsIfAvailable),
        ("starttls", SecurityMode.StartTls),
        ("ssl", SecurityMode.Ssl),
    ];

    public static string ValidValues { get; } = string.Join(", ", Names.Select(n => n.Name));

    public static bool TryParse(string value, out SecurityMode mode)
    {
        foreach (var (name, candidate) in Names)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = SecurityMode.Auto;
        return false;
    }

    public static string ToCliName(this SecurityMode mode) => Names.First(n => n.Mode == mode).Name;

    public static SecureSocketOptions ToSocketOptions(this SecurityMode mode) => mode switch
    {
        SecurityMode.None => SecureSocketOptions.None,
        SecurityMode.Auto => SecureSocketOptions.Auto,
        SecurityMode.StartTlsIfAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        SecurityMode.StartTls => SecureSocketOptions.StartTls,
        SecurityMode.Ssl => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped security mode"),
    };
}
