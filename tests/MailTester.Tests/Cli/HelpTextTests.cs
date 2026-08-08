using MailTester.Cli;
using MailTester.Errors;

namespace MailTester.Tests.Cli;

public class HelpTextTests
{
    // Every flag ArgParser accepts. A flag added to the parser and not to the help
    // is a flag nobody will find, so this list is deliberately duplicated here.
    static readonly string[] EveryFlag =
    [
        "--host", "--port", "--security", "--timeout", "--ehlo-domain", "--allow-invalid-cert",
        "--auth", "--user", "--pass",
        "--from", "--to", "--subject", "--body",
        "--probe", "--log-file", "--show-secrets", "--no-color", "--help",
    ];

    [Theory]
    [MemberData(nameof(FlagCases))]
    public void Help_documents_every_flag_the_parser_accepts(string flag)
    {
        Assert.Contains(flag, HelpText.Render());
    }

    public static TheoryData<string> FlagCases()
    {
        var data = new TheoryData<string>();
        foreach (var flag in EveryFlag)
            data.Add(flag);
        return data;
    }

    [Fact]
    public void Help_explains_what_each_security_mode_does_including_auto()
    {
        var help = HelpText.Render();

        Assert.Contains("starttls-if-available", help);
        Assert.Contains("SslOnConnect", help);
        Assert.Contains("StartTlsWhenAvailable", help);
        // The one value whose behaviour is not obvious from its name.
        Assert.Contains("465", help);
    }

    [Fact]
    public void Help_lists_every_auth_mechanism()
    {
        var help = HelpText.Render();

        foreach (var mechanism in AuthMechanisms.ValidValues.Split(", "))
            Assert.Contains(mechanism, help);
    }

    [Fact]
    public void Help_lists_every_exit_code_with_its_number()
    {
        var help = HelpText.Render();

        foreach (var code in Enum.GetValues<ExitCode>())
            Assert.Contains($"  {(int)code}  ", help);
    }

    [Fact]
    public void Help_shows_a_runnable_probe_example_and_a_runnable_send_example()
    {
        var help = HelpText.Render();

        Assert.Contains("mail-tester --probe --host", help);
        Assert.Contains("--security starttls", help);
    }

    [Fact]
    public void Help_ends_with_a_single_newline_so_it_composes_with_Write()
    {
        var help = HelpText.Render();

        Assert.EndsWith("\n", help);
        Assert.DoesNotContain("\n\n\n", help.ReplaceLineEndings("\n"));
    }
}
