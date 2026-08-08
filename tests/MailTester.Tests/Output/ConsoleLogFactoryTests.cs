using MailTester.Cli;
using MailTester.Output;

namespace MailTester.Tests.Output;

public class ConsoleLogFactoryTests
{
    static CliOptions Options(bool noColor = false) => new() { Host = "smtp.example.com", NoColor = noColor };

    [Fact]
    public void The_no_color_flag_alone_suppresses_colour()
    {
        var colorizer = ConsoleLogFactory.PickColorizer(Options(noColor: true), isOutputRedirected: false);

        Assert.IsType<NullColorizer>(colorizer);
    }

    [Fact]
    public void A_redirected_output_alone_suppresses_colour()
    {
        var colorizer = ConsoleLogFactory.PickColorizer(Options(), isOutputRedirected: true);

        Assert.IsType<NullColorizer>(colorizer);
    }

    [Fact]
    public void The_NO_COLOR_environment_variable_alone_suppresses_colour()
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        Environment.SetEnvironmentVariable("NO_COLOR", "1");

        try
        {
            var colorizer = ConsoleLogFactory.PickColorizer(Options(), isOutputRedirected: false);

            Assert.IsType<NullColorizer>(colorizer);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", previous);
        }
    }

    [Fact]
    public void With_none_of_the_three_conditions_the_real_console_colorizer_is_used()
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        Environment.SetEnvironmentVariable("NO_COLOR", null);

        try
        {
            var colorizer = ConsoleLogFactory.PickColorizer(Options(), isOutputRedirected: false);

            Assert.IsType<ConsoleColorizer>(colorizer);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", previous);
        }
    }
}
