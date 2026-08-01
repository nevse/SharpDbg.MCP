namespace SharpDbg.MCP.TestApp;

/// <summary>
/// Debuggee used by the integration tests. It loops forever printing a tick, so a test can tell
/// whether the process is running or suspended by watching its output.
/// Lines are located by the marker comments below - never hard-code line numbers in tests.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        // Off by default, so tests that are not about exceptions are not disturbed by them
        var throwEachIteration = args.Contains("--throw");

        Console.WriteLine($"PID={Environment.ProcessId}");
        Console.Out.Flush();

        var counter = 0;
        while (true)
        {
            counter = Work(counter);

            if (throwEachIteration)
                ThrowAndCatch(counter);

            Console.WriteLine($"tick {counter}");
            Console.Out.Flush();
            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// Throws and handles the exception itself, which is what a debugger that breaks on every
    /// first-chance exception has to cope with: nothing is actually wrong with this program.
    /// </summary>
    private static void ThrowAndCatch(int counter)
    {
        try
        {
            throw new InvalidOperationException($"thrown on iteration {counter}"); // THROW-TARGET
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int Work(int current)
    {
        var next = current + 1; // BREAKPOINT-TARGET
        var label = $"n={next}"; // STEP-TARGET
        var point = new Point(next, label.Length);
        return point.X; // EXPAND-TARGET
    }

    /// <summary>
    /// Gives the expansion tests an object with members to walk into
    /// </summary>
    private sealed record Point(int X, int Y);
}
