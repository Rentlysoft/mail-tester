using MailTester.Output;

namespace MailTester.Tests.Output;

public class ConsoleLogTests
{
    sealed class RecordingColorizer : IColorizer
    {
        public List<string> Calls { get; } = [];

        public void Set(ConsoleColor color) => Calls.Add($"set:{color}");

        public void Reset() => Calls.Add("reset");
    }

    sealed class DisposeTrackingWriter : StringWriter
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static ConsoleLog Build(StringWriter output, TextWriter? file = null, IColorizer? colorizer = null, long ms = 1234) =>
        new(output, file, colorizer ?? new NullColorizer(), () => TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Line_prefixes_a_relative_timestamp_and_a_padded_level()
    {
        var output = new StringWriter();
        using var log = Build(output);

        log.Line(LogLevel.Info, "mail-tester 1.0");

        Assert.Equal("[00:01.234] INFO  mail-tester 1.0", output.ToString().TrimEnd());
    }

    [Fact]
    public void Short_level_names_are_padded_so_messages_stay_aligned()
    {
        var output = new StringWriter();
        using var log = Build(output);

        log.Line(LogLevel.Ok, "conectado");
        log.Line(LogLevel.Cert, "CN=smtp.foo.com");

        var lines = output.ToString().TrimEnd().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("[00:01.234] OK    conectado", lines[0]);
        Assert.Equal("[00:01.234] CERT  CN=smtp.foo.com", lines[1]);
        Assert.Equal(lines[0].IndexOf("conectado", StringComparison.Ordinal),
                     lines[1].IndexOf("CN=", StringComparison.Ordinal));
    }

    [Fact]
    public void Timestamps_roll_over_into_minutes()
    {
        var output = new StringWriter();
        using var log = Build(output, ms: 65_432);

        log.Line(LogLevel.Info, "x");

        Assert.StartsWith("[01:05.432]", output.ToString());
    }

    [Fact]
    public void Protocol_lines_carry_their_prefix_and_no_timestamp()
    {
        var output = new StringWriter();
        using var log = Build(output);

        log.Protocol("S: ", "220 smtp.foo.com ESMTP");
        log.Protocol("C: ", "EHLO desktop-abc");

        var lines = output.ToString().TrimEnd().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("S: 220 smtp.foo.com ESMTP", lines[0]);
        Assert.Equal("C: EHLO desktop-abc", lines[1]);
    }

    [Fact]
    public void Banner_frames_the_title_between_two_rules_of_equal_width()
    {
        var output = new StringWriter();
        using var log = Build(output);

        log.Banner("FALLA EN FASE: TLS HANDSHAKE");

        var lines = output.ToString().TrimEnd().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(lines[0], lines[2]);
        Assert.All(lines[0], c => Assert.Equal('═', c));
        Assert.Contains("FALLA EN FASE: TLS HANDSHAKE", lines[1]);
    }

    [Fact]
    public void Everything_written_to_the_console_is_mirrored_to_the_log_file()
    {
        var output = new StringWriter();
        var file = new StringWriter();
        using var log = Build(output, file);

        log.Line(LogLevel.Step, "1/7 DNS");
        log.Protocol("S: ", "220 hola");
        log.Banner("TITULO");
        log.Blank();

        Assert.Equal(output.ToString(), file.ToString());
    }

    [Fact]
    public void Each_line_is_wrapped_in_a_colour_set_and_reset()
    {
        var output = new StringWriter();
        var colorizer = new RecordingColorizer();
        using var log = Build(output, file: null, colorizer: colorizer);

        log.Line(LogLevel.Fail, "roto");

        Assert.Equal(["set:Red", "reset"], colorizer.Calls);
    }

    [Fact]
    public void Every_level_has_a_colour_and_a_label()
    {
        var output = new StringWriter();
        var colorizer = new RecordingColorizer();
        using var log = Build(output, file: null, colorizer: colorizer);

        foreach (var level in Enum.GetValues<LogLevel>())
            log.Line(level, "x");

        // One set and one reset per line: no level falls through uncoloured.
        Assert.Equal(Enum.GetValues<LogLevel>().Length * 2, colorizer.Calls.Count);

        // Every level renders a timestamp, a non-empty uppercase label, and the message.
        var lines = output.ToString().TrimEnd().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(Enum.GetValues<LogLevel>().Length, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\[\d\d:\d\d\.\d\d\d\] [A-Z]{2,4} +x$", line));
    }

    [Fact]
    public void Disposing_the_log_disposes_the_file_writer_so_the_file_is_flushed()
    {
        var output = new StringWriter();
        var file = new DisposeTrackingWriter();

        using (var log = Build(output, file))
            log.Line(LogLevel.Info, "x");

        Assert.True(file.Disposed);
    }

    [Fact]
    public void Blank_writes_an_empty_line()
    {
        var output = new StringWriter();
        using var log = Build(output);

        log.Blank();

        Assert.Equal(Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Text_writes_the_line_verbatim_with_no_timestamp_or_level()
    {
        var output = new StringWriter();
        using var log = Build(output);

        log.Text("  1) --port 587 --security starttls");

        Assert.Equal("  1) --port 587 --security starttls", output.ToString().TrimEnd());
    }
}
