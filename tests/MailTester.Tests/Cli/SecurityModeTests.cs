using MailKit.Security;
using MailTester.Cli;

namespace MailTester.Tests.Cli;

public class SecurityModeTests
{
    [Theory]
    [InlineData("none", SecurityMode.None)]
    [InlineData("auto", SecurityMode.Auto)]
    [InlineData("starttls-if-available", SecurityMode.StartTlsIfAvailable)]
    [InlineData("starttls", SecurityMode.StartTls)]
    [InlineData("ssl", SecurityMode.Ssl)]
    [InlineData("STARTTLS", SecurityMode.StartTls)]
    internal void TryParse_accepts_every_documented_name_case_insensitively(string input, SecurityMode expected)
    {
        Assert.True(SecurityModes.TryParse(input, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("")]
    [InlineData("start-tls")]
    public void TryParse_rejects_unknown_names(string input)
    {
        Assert.False(SecurityModes.TryParse(input, out _));
    }

    [Fact]
    public void ValidValues_lists_all_five_names_for_error_messages()
    {
        Assert.Equal("none, auto, starttls-if-available, starttls, ssl", SecurityModes.ValidValues);
    }

    [Theory]
    [InlineData(SecurityMode.None, SecureSocketOptions.None)]
    [InlineData(SecurityMode.Auto, SecureSocketOptions.Auto)]
    [InlineData(SecurityMode.StartTlsIfAvailable, SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData(SecurityMode.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(SecurityMode.Ssl, SecureSocketOptions.SslOnConnect)]
    internal void ToSocketOptions_maps_one_to_one_with_MailKit(SecurityMode mode, SecureSocketOptions expected)
    {
        Assert.Equal(expected, mode.ToSocketOptions());
    }

    [Fact]
    public void ToCliName_round_trips_through_TryParse()
    {
        foreach (var mode in Enum.GetValues<SecurityMode>())
        {
            Assert.True(SecurityModes.TryParse(mode.ToCliName(), out var parsed));
            Assert.Equal(mode, parsed);
        }
    }
}
