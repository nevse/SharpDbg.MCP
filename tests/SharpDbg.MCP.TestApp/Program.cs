namespace SharpDbg.MCP.TestApp;

/// <summary>
/// Debuggee used by the integration tests. It loops forever printing a tick, so a test can tell
/// whether the process is running or suspended by watching its output.
/// Lines are located by the marker comments below - never hard-code line numbers in tests.
/// </summary>
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine($"PID={Environment.ProcessId}");
        Console.Out.Flush();

        var counter = 0;
        while (true)
        {
            counter = Work(counter);
            Console.WriteLine($"tick {counter}");
            Console.Out.Flush();
            Thread.Sleep(150);
        }
    }

    private static int Work(int current)
    {
        var next = current + 1; // BREAKPOINT-TARGET
        var label = $"n={next}"; // STEP-TARGET
        return next + label.Length - label.Length;
    }
}