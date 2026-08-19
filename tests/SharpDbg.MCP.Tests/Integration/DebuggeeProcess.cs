using System.Diagnostics;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// Starts SharpDbg.MCP.TestApp as a real debuggee and tracks its output, so tests can assert
/// whether the process is actually suspended rather than only trusting the debugger's own state.
/// </summary>
internal sealed class DebuggeeProcess : IDisposable
{
    private static readonly TimeSpan AttachSettleTime = TimeSpan.FromMilliseconds(500);

    private readonly Process _process;
    private int _outputLines;

    private DebuggeeProcess(Process process)
    {
        _process = process;
    }

    public int ProcessId => _process.Id;

    /// <summary>
    /// Number of lines the debuggee has printed so far
    /// </summary>
    public int OutputLines => Volatile.Read(ref _outputLines);

    /// <summary>
    /// Starts the debuggee. Pass "--throw" to have it throw and catch an exception every iteration.
    /// </summary>
    public static DebuggeeProcess Start(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(TestPaths.TestAppAssembly);

        return StartCore(startInfo, arguments);
    }

    /// <summary>
    /// Starts the same program through its apphost rather than the muxer. Which of the two a debuggee
    /// runs as is a variable in its own right, because the operating system treats them differently:
    /// the muxer is Developer ID signed and carries com.apple.security.get-task-allow, while the
    /// apphost the SDK produces is ad-hoc signed with no entitlements at all.
    /// </summary>
    public static DebuggeeProcess StartAppHost(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(TestPaths.TestAppExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        return StartCore(startInfo, arguments);
    }

    private static DebuggeeProcess StartCore(ProcessStartInfo startInfo, string[] arguments)
    {
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the debuggee process");

        var debuggee = new DebuggeeProcess(process);

        // The pipe must be drained continuously - a full stdout buffer would block the debuggee
        // and look exactly like a process suspended on a breakpoint.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                Interlocked.Increment(ref debuggee._outputLines);
        };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        debuggee.WaitUntilRunning();

        // Let the debuggee finish its startup burst of assembly loads before a debugger attaches.
        // The shim crashes in ShimProcess::QueueFakeAttachEvents while replaying the synthetic
        // module load events for an attach (see the backlog), and attaching into that burst makes it
        // measurably more likely: without this wait, three of six full runs died, with it one of
        // six, which is the same rate as before these tests existed. It is a way to be hit less
        // often, not a fix.
        Thread.Sleep(AttachSettleTime);

        return debuggee;
    }

    /// <summary>
    /// Wait until the debuggee has produced output, so it is past startup before we attach
    /// </summary>
    private void WaitUntilRunning()
    {
        if (!SpinUntil(() => OutputLines > 1, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("Debuggee process did not start producing output");
    }

    /// <summary>
    /// Returns the number of lines printed during the given window. Zero means the process is
    /// genuinely suspended.
    /// </summary>
    public int CountOutputDuring(TimeSpan window)
    {
        var before = OutputLines;
        Thread.Sleep(window);
        return OutputLines - before;
    }

    /// <summary>
    /// How long the debuggee took to print again, for a failure that has to say which of two things
    /// went wrong: a resume that was merely slow - a Windows runner pays a DAP round trip for every
    /// exception in some modes - or one that never arrived. Returns null when nothing was printed
    /// within the timeout.
    /// </summary>
    public string DescribeResumption(TimeSpan timeout)
    {
        var before = OutputLines;
        var started = DateTime.UtcNow;

        return SpinUntil(() => OutputLines > before, timeout)
            ? $"printed again after a further {(DateTime.UtcNow - started).TotalSeconds:0.0}s"
            : $"printed nothing for a further {timeout.TotalSeconds:0}s";
    }

    public static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (condition())
                return true;

            if (DateTime.UtcNow >= deadline)
                return false;

            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);

            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Process already gone
        }
        finally
        {
            _process.Dispose();
        }
    }
}
