using System.Diagnostics;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// Launching a program, as opposed to attaching to one that is already running. What these are
/// really about is the startup of the debuggee: everything before the first line of Main is
/// unreachable by attaching, because there is nothing to attach to yet.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class LaunchIntegrationTests
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private static DebugSession CreateSession() => new(1, new ServerConfiguration());

    private static ExecutionState WaitForStop(DebugSession session)
    {
        var stopped = DebuggeeProcess.SpinUntil(
            () =>
            {
                var current = session.GetExecutionState();
                return current.Started && !current.IsRunning;
            },
            StopTimeout);

        var state = session.GetExecutionState();

        Assert.IsTrue(stopped, $"Program never stopped. State: running={state.IsRunning}, reason={state.StopReason}");
        return state;
    }

    [TestMethod]
    public async Task Launch_BreakpointSetBeforeStart_StopsBeforeTheProgramPrintsAnything()
    {
        var line = TestPaths.FindMarkerLine("STARTUP-TARGET");

        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);

        var state = session.GetExecutionState();
        Assert.IsFalse(state.Started, "A launched program must not be running before start_program");
        Assert.IsFalse(state.IsRunning);

        var breakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line);
        Assert.IsFalse(breakpoint.Verified,
            "Nothing can bind before the program runs - no modules are loaded yet");

        session.Start();

        var stopped = WaitForStop(session);

        Assert.AreEqual("breakpoint", stopped.StopReason);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{line}", stopped.CurrentLocation);
        Assert.AreEqual(breakpoint.Id, stopped.LastBreakpoint?.BreakpointId);

        // The marker sits on the program's first statement, which prints its pid. Nothing having
        // been printed is what proves the breakpoint was in place before that statement ran.
        Assert.IsEmpty(session.ReadOutput(100),
            "The program was allowed to run before the breakpoint took effect");
    }

    /// <summary>
    /// A self-contained or single-file publish produces no .dll to run, only an apphost. SharpDbg
    /// handed everything to the dotnet muxer until 0.1.12, which attached the debugger to the muxer
    /// rather than to the program.
    /// </summary>
    [TestMethod]
    public async Task Launch_AppHostRatherThanAssembly_StopsAtTheBreakpoint()
    {
        var line = TestPaths.FindMarkerLine("STARTUP-TARGET");

        // Launching an apphost hangs on GitHub's macOS runner and nowhere else - not on Linux, not on
        // Windows, and not on macOS off the runner, where this is verified on every local run. The
        // debuggee starts and opens its diagnostic port; the runtime-startup callback never arrives.
        // Ruled out by measurement rather than argument: the runner's SDK and runtime, the test
        // sequence, stale diagnostic sockets, its core count, its macOS version, and code signing -
        // the runner is the more permissive machine, with SIP off and developer mode on, and its
        // apphost is signed identically. See UPSTREAM.md defect 17.
        //
        // Skipped only on the runner, because that is the only place it fails and the local run is
        // what keeps the capability covered. It is skipped rather than left red for the reason the
        // one launch that fails poisons the rest: ClrDebugExtensions keeps its runtime-startup state
        // in a static, so every later launch in the same test host hangs too, and four tests report
        // a defect that belongs to one.
        if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.Inconclusive("Launching an apphost hangs on the macOS runner - UPSTREAM.md defect 17");

        Assert.IsTrue(File.Exists(TestPaths.TestAppExecutable),
            $"The test app's apphost must be built: {TestPaths.TestAppExecutable}");

        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppExecutable);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        session.Start();

        var stopped = WaitForStop(session);

        Assert.AreEqual("breakpoint", stopped.StopReason);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{line}", stopped.CurrentLocation);
    }

    [TestMethod]
    public async Task Launch_ProgramOutput_IsReadableThroughTheSession()
    {
        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);
        session.Start();

        var printed = DebuggeeProcess.SpinUntil(
            () => session.ReadOutput(100).Any(l => l.Text.StartsWith("tick", StringComparison.Ordinal)),
            StopTimeout);

        Assert.IsTrue(printed,
            "A launched program's streams are redirected into the debug adapter, so the session is "
            + "the only place its output can be read");
    }

    /// <summary>
    /// get_program_output asks for at most max_lines of the most recent output, which is the
    /// truncating branch of ReadOutput. Every other read in this suite asks for more than the
    /// buffer holds and never reaches it, so returning the oldest lines instead of the newest
    /// would leave the whole suite green while a caller watching a running program saw its first
    /// lines forever.
    /// </summary>
    [TestMethod]
    public async Task Launch_MoreOutputThanAskedFor_ReturnsTheNewestLines()
    {
        const int asked = 5;

        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);
        session.Start();

        // Two lines past the tail, not one: at exactly one the skip offset is 1, which a hard-coded
        // Skip(1) satisfies as well as the real arithmetic does. Costs one extra tick.
        var ticked = DebuggeeProcess.SpinUntil(
            () => session.ReadOutput(int.MaxValue).Count > asked + 1,
            StopTimeout);

        Assert.IsTrue(ticked, $"The program never printed more than {asked + 1} lines");

        // Suspend it, or it keeps printing across the reads below and there is no fixed tail to
        // compare against. Pausing alone is not a barrier and waiting for a stop buys nothing:
        // Pause sets the session not-running itself and raises no stop event, so the wait returns
        // at once while what the program already wrote is still in flight through the adapter's
        // stream callbacks. Bracket the tail read between two full reads and compare only when
        // those match - that proves nothing landed in between, where waiting out a quiet interval
        // would only make the race rarer.
        session.Pause();

        IReadOnlyList<OutputLine> all = [];
        IReadOnlyList<OutputLine> newest = [];

        var bracketed = DebuggeeProcess.SpinUntil(
            () =>
            {
                var before = session.ReadOutput(int.MaxValue);
                newest = session.ReadOutput(asked);
                all = session.ReadOutput(int.MaxValue);

                return before.SequenceEqual(all);
            },
            StopTimeout);

        Assert.IsTrue(bracketed, "Output kept arriving across every attempt to read the buffer");

        Assert.HasCount(asked, newest);
        CollectionAssert.AreEqual(
            all.TakeLast(asked).ToList(),
            newest.ToList(),
            "ReadOutput must return the tail of the buffer, not the head");

        // The pid is printed once, before the ticks, so it is always the oldest line held. Seeing
        // it here is what a Skip-for-Take swap looks like from the outside.
        Assert.IsFalse(newest.Any(l => l.Text.StartsWith("PID=", StringComparison.Ordinal)),
            "ReadOutput returned the oldest lines, not the newest");
    }

    [TestMethod]
    public async Task Launch_Detach_KillsTheProgramItStarted()
    {
        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);
        session.Start();

        var processId = WaitForReportedProcessId(session);

        // Detaching while the program is running is the case that needs the pause: terminating a
        // running debuggee fails inside ICorDebug and the failure is not reported anywhere.
        // The return is what the tools turn into program_may_be_running, and it is reported rather
        // than observed, so asserting the process died is not enough to pin it: this is the one
        // place a real program is launched, started and killed, so it is where the two can be
        // checked against each other.
        var suspended = session.Detach();

        Assert.IsTrue(DebuggeeProcess.SpinUntil(() => !IsAlive(processId), TimeSpan.FromSeconds(15)),
            $"Process {processId} outlived the session that launched it");

        Assert.IsTrue(suspended,
            "Detach reported the program as possibly still running, but it was killed");
    }

    [TestMethod]
    public async Task Launch_BeforeStart_OperationsNeedingAProcessSayWhy()
    {
        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);

        var threads = Assert.Throws<InvalidOperationException>(() => session.GetThreads());
        StringAssert.Contains(threads.Message, "start_program");

        var resume = Assert.Throws<InvalidOperationException>(() => session.Continue());
        StringAssert.Contains(resume.Message, "start_program");
    }

    [TestMethod]
    public async Task Launch_TwiceInOneSession_IsRefused()
    {
        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Launch(TestPaths.TestAppAssembly));
    }

    /// <summary>
    /// The debuggee prints its own pid, which is the only way a test can find it: SharpDbg sends no
    /// process event, so what it launched is never named to the client.
    /// </summary>
    private static int WaitForReportedProcessId(DebugSession session)
    {
        string? line = null;

        var reported = DebuggeeProcess.SpinUntil(
            () =>
            {
                line = session.ReadOutput(100)
                    .Select(l => l.Text)
                    .FirstOrDefault(t => t.StartsWith("PID=", StringComparison.Ordinal));

                return line != null;
            },
            StopTimeout);

        Assert.IsTrue(reported, "The launched program never reported its process id");

        return int.Parse(line!["PID=".Length..]);
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
