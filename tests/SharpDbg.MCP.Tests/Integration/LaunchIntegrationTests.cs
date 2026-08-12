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

    [TestMethod]
    public async Task Launch_Detach_KillsTheProgramItStarted()
    {
        using var session = CreateSession();

        await session.Launch(TestPaths.TestAppAssembly);
        session.Start();

        var processId = WaitForReportedProcessId(session);

        // Detaching while the program is running is the case that needs the pause: terminating a
        // running debuggee fails inside ICorDebug and the failure is not reported anywhere.
        session.Detach();

        Assert.IsTrue(DebuggeeProcess.SpinUntil(() => !IsAlive(processId), TimeSpan.FromSeconds(15)),
            $"Process {processId} outlived the session that launched it");
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
