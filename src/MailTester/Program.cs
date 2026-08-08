using MailTester.Modes;

namespace MailTester;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Handle the interrupt ourselves so the run can report where it was cut off.
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var exitCode = await Application.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
        return (int)exitCode;
    }
}
