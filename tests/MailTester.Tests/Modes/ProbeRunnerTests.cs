using MailTester.Cli;
using MailTester.Errors;
using MailTester.Modes;
using MailTester.Output;
using MailTester.Tests.Fakes;

namespace MailTester.Tests.Modes;

public class ProbeRunnerTests
{
    static CliOptions Options(int port, string? user = null, string? password = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        PortSpecified = true,
        Security = SecurityMode.None,
        SecuritySpecified = true,
        Probe = true,
        User = user,
        Password = password,
        TimeoutSeconds = 5,
    };

    static async Task<(ExitCode Code, string Text)> RunAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);

        var code = await ProbeRunner.RunAsync(options, log, cancellationToken);
        return (code, output.ToString());
    }

    /// <summary>Counts how many times <paramref name="marker"/> starts within [start, end) of
    /// <paramref name="text"/>, so a test can assert that something belonging to one attempt
    /// never leaks into another attempt's slice of the log.</summary>
    static int Occurrences(string text, string marker, int start, int end)
    {
        var count = 0;
        var index = start;

        while (true)
        {
            index = text.IndexOf(marker, index, StringComparison.Ordinal);
            if (index < 0 || index >= end)
                return count;

            count++;
            index += marker.Length;
        }
    }

    [Fact]
    public async Task A_working_combination_yields_success_and_a_recommendation()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (code, text) = await RunAsync(Options(server.Port, "bob@fake.local", "s3cr3t"));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Recomendado", text);
        Assert.Contains("MATRIZ DE RESULTADOS", text);
    }

    [Fact]
    public async Task Probe_never_sends_a_message()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        await RunAsync(Options(server.Port));

        Assert.DoesNotContain(server.CommandsReceived, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.CommandsReceived, c => c.Equals("DATA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_no_combination_works_the_exit_code_comes_from_the_most_advanced_failure()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsAuth());

        var (code, text) = await RunAsync(Options(server.Port, "bob@fake.local", "wrong"));

        Assert.Equal(ExitCode.AuthenticationFailure, code);
        Assert.Contains("Ninguna combinación funcionó", text);
    }

    [Fact]
    public async Task Each_attempt_is_announced_with_its_own_banner()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (_, text) = await RunAsync(Options(server.Port));

        Assert.Contains("INTENTO 1/1", text);
    }

    [Fact]
    public async Task Three_combinations_against_one_port_run_one_after_another_without_interleaving()
    {
        // Naming a port but not a security mode sweeps starttls, ssl and none against that one
        // port -- three attempts hitting the same fake, one connection at a time.
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working(), sessions: 3);

        var options = new CliOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PortSpecified = true,
            Probe = true,
            TimeoutSeconds = 5,
        };

        var (code, text) = await RunAsync(options);

        var banner1 = text.IndexOf("INTENTO 1/3", StringComparison.Ordinal);
        var banner2 = text.IndexOf("INTENTO 2/3", StringComparison.Ordinal);
        var banner3 = text.IndexOf("INTENTO 3/3", StringComparison.Ordinal);

        Assert.True(banner1 >= 0, "El intento 1/3 no se anunció.");
        Assert.True(banner1 < banner2, "El intento 2/3 debe anunciarse después del 1/3.");
        Assert.True(banner2 < banner3, "El intento 3/3 debe anunciarse después del 2/3.");

        // Every attempt logs this step first, regardless of how it then succeeds or fails. If
        // the attempts ever ran concurrently instead of one after another, their dialogues
        // would interleave and an occurrence would land outside its own attempt's segment.
        const string dnsStep = "1/6 Resolviendo DNS de 127.0.0.1";
        Assert.Equal(1, Occurrences(text, dnsStep, banner1, banner2));
        Assert.Equal(1, Occurrences(text, dnsStep, banner2, banner3));
        Assert.Equal(1, Occurrences(text, dnsStep, banner3, text.Length));

        // Of the three, only the third (plain, no TLS) actually works against this script, and
        // the sweep still finds and recommends it.
        Assert.Equal(ExitCode.Success, code);
    }

    [Fact]
    public async Task Cancellation_stops_the_sweep_instead_of_marching_on_to_the_next_combination()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working(), sessions: 3);

        var options = new CliOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PortSpecified = true,
            Probe = true,
            TimeoutSeconds = 5,
        };

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var (_, text) = await RunAsync(options, cancellation.Token);

        Assert.Contains("INTENTO 1/3", text);
        Assert.DoesNotContain("INTENTO 2/3", text);
        Assert.DoesNotContain("INTENTO 3/3", text);
    }
}
