using MailTester.Cli;

namespace MailTester.Tests.Cli;

public class AuthMechanismTests
{
    [Theory]
    [InlineData("auto", AuthMechanism.Auto)]
    [InlineData("plain", AuthMechanism.Plain)]
    [InlineData("login", AuthMechanism.Login)]
    [InlineData("cram-md5", AuthMechanism.CramMd5)]
    [InlineData("ntlm", AuthMechanism.Ntlm)]
    [InlineData("none", AuthMechanism.None)]
    [InlineData("CRAM-MD5", AuthMechanism.CramMd5)]
    internal void TryParse_accepts_every_documented_name_case_insensitively(string input, AuthMechanism expected)
    {
        Assert.True(AuthMechanisms.TryParse(input, out var mechanism));
        Assert.Equal(expected, mechanism);
    }

    [Theory]
    [InlineData("crammd5")]
    [InlineData("xoauth2")]
    [InlineData("")]
    public void TryParse_rejects_unknown_names(string input)
    {
        Assert.False(AuthMechanisms.TryParse(input, out _));
    }

    [Fact]
    public void ValidValues_lists_all_six_names_for_error_messages()
    {
        Assert.Equal("auto, plain, login, cram-md5, ntlm, none", AuthMechanisms.ValidValues);
    }

    [Theory]
    [InlineData(AuthMechanism.Plain, "PLAIN")]
    [InlineData(AuthMechanism.Login, "LOGIN")]
    [InlineData(AuthMechanism.CramMd5, "CRAM-MD5")]
    [InlineData(AuthMechanism.Ntlm, "NTLM")]
    internal void ToSaslName_returns_the_wire_name_for_forced_mechanisms(AuthMechanism mechanism, string expected)
    {
        Assert.Equal(expected, mechanism.ToSaslName());
    }

    [Theory]
    [InlineData(AuthMechanism.Auto)]
    [InlineData(AuthMechanism.None)]
    internal void ToSaslName_returns_null_when_no_mechanism_is_forced(AuthMechanism mechanism)
    {
        Assert.Null(mechanism.ToSaslName());
    }
}
