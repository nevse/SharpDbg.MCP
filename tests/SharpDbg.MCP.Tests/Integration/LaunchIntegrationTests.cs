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

        // This used to hang on GitHub's macOS runner and was skipped there. The cause was not in the
        // debugger: macOS would not hand over the debuggee's task port because the apphost carried no
        // com.apple.security.get-task-allow, and it refused by blocking, so task_for_pid never
        // returned and DebugActiveProcess never came back. The test app is now re-signed with that
        // entitlement at build time, which is why this runs everywhere again. UPSTREAM.md defect 17.
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

    /// <summary>
    /// Attaching to an apphost, which is the one combination the suite never covered. Every other
    /// attach test targets a debuggee started as `dotnet app.dll`, so "launching fails and attaching
    /// works" has always been confounded with "the target is an apphost" against "the target is the
    /// muxer". This separates them: the debuggee is an apphost and the debugger attaches to it
    /// already running, so the launch path is not involved at all.
    ///
    /// That mattered: on the macOS runner an apphost debuggee hung the same way whether it was
    /// launched or attached to, which is what proved the launch path innocent and put the cause in
    /// the target's entitlements instead.
    /// </summary>
    [TestMethod]
    public async Task Attach_ToAnAppHost_StopsAtTheBreakpoint()
    {
        Assert.IsTrue(File.Exists(TestPaths.TestAppExecutable),
            $"The test app's apphost must be built: {TestPaths.TestAppExecutable}");

        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.StartAppHost();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var breakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line);
        Assert.IsTrue(breakpoint.Verified, $"Breakpoint was not verified: {breakpoint.Message}");

        // Not the WaitForStop above: that one also waits for Started, which start_program sets and an
        // attach never does.
        var reached = DebuggeeProcess.SpinUntil(() => !session.GetExecutionState().IsRunning, StopTimeout);
        var stopped = session.GetExecutionState();

        Assert.IsTrue(reached,
            $"Never stopped. State: running={stopped.IsRunning}, reason={stopped.StopReason}");
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
        //
        // Asserted rather than discarded: Pause is bounded now and reports an unconfirmed pause by
        // returning false, so ignoring it would leave the program running and fail below as a
        // baffling output race instead of as the pause that did not land.
        Assert.IsTrue(session.Pause(), "The program was never paused, so there is no fixed tail to read");

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

        var processId = WaitForPrintedProcessId(session);

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
    /// A launched program had no pid to report until SharpDbg 0.1.14: nothing said what had been
    /// started, so start_program answered with null and a caller had no way to find the process it
    /// had just created. The pid now arrives as a DAP process event, and it arrives before start
    /// returns, which is why nothing here waits for it.
    /// </summary>
    [TestMethod]
    public async Task Launch_Start_NamesTheProcessItCreated()
    {
        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);

        Assert.IsNull(session.ProcessId,
            "Nothing has been started, so there is no process to name yet");

        session.Start();

        // Read with no spin on purpose. The event arrives while configurationDone is being handled,
        // so a pid that needed waiting for would be a change in behaviour worth failing on.
        var reported = session.ProcessId;
        Assert.IsNotNull(reported, "The debugger never said what it started");

        Assert.AreEqual(WaitForPrintedProcessId(session), reported.Value,
            "The debugger named a different process than the one it started");
    }

    /// <summary>
    /// The exit code, which read as null for every program until SharpDbg 0.1.14 reported it. The
    /// debuggee is asked for a specific non-zero code, because zero is what a missing code used to
    /// look like and would pass whether this works or not.
    /// </summary>
    [TestMethod]
    public async Task Launch_WhenTheProgramExits_ReportsTheCodeItReturned()
    {
        const int expected = 7;

        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly, [$"--exit-code={expected}"]);
        session.Start();

        var exited = DebuggeeProcess.SpinUntil(
            () => session.GetExecutionState().StopReason == "exited", StopTimeout);

        var state = session.GetExecutionState();

        Assert.IsTrue(exited, $"The program never exited. State: reason={state.StopReason}");
        Assert.AreEqual(expected, state.ExitCode);
        Assert.IsFalse(state.IsRunning);
    }

    /// <summary>
    /// The pid the debuggee printed for itself. Since SharpDbg 0.1.14 the session knows it too, from
    /// the debugger's process event, and this is what that can be checked against - the debugger
    /// naming some process is worth nothing unless it is naming the right one.
    /// </summary>
    private static int WaitForPrintedProcessId(DebugSession session)
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
