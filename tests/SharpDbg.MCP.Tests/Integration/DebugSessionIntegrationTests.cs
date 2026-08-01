using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// Drives a real debuggee through DebugSession. These cover the layer where the MCP server talks
/// to ManagedDebugger - the unit tests only reach configuration and input validation, which is why
/// every bug in this file's history shipped unnoticed.
///
/// Attaching a debugger is process-wide, so these must never run in parallel.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class DebugSessionIntegrationTests
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(1);

    private static DebugSession CreateSession() => new(1, new ServerConfiguration());

    private static ExecutionState WaitForStop(DebugSession session)
    {
        var stopped = DebuggeeProcess.SpinUntil(() => !session.GetExecutionState().IsRunning, StopTimeout);
        var state = session.GetExecutionState();

        Assert.IsTrue(stopped, $"Process never stopped. State: running={state.IsRunning}, reason={state.StopReason}");
        return state;
    }

    /// <summary>
    /// Regression: breakpoint hits arrive on ManagedDebugger.OnStopped2, not OnStopped. When only
    /// OnStopped was handled the debuggee really did freeze, but the session still reported
    /// is_running=true with no location, so callers could not tell a breakpoint had been hit.
    /// </summary>
    [TestMethod]
    public async Task Breakpoint_WhenHit_IsReportedAsStoppedWithLocation()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var breakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line);
        Assert.IsTrue(breakpoint.Verified, $"Breakpoint was not verified: {breakpoint.Message}");

        var state = WaitForStop(session);

        Assert.AreEqual("breakpoint", state.StopReason);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{line}", state.CurrentLocation);
        Assert.IsNotNull(state.StoppedThreadId, "Callers need the stopped thread to request a stack trace");
        Assert.IsNotNull(state.LastBreakpoint);
        Assert.AreEqual(line, state.LastBreakpoint.Line);

        // The reported state must match reality, not just the debugger's bookkeeping
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow), "Debuggee kept running while reported stopped");
    }

    [TestMethod]
    public async Task Breakpoint_WhenHit_ExposesStackVariablesAndEvaluation()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var state = WaitForStop(session);

        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        Assert.IsGreaterThan(0, frames.Count);
        StringAssert.Contains(frames[0].Name, "Work");
        Assert.AreEqual(line, frames[0].Line);

        var variables = await session.GetVariables(frames[0].Id);
        var current = variables.SingleOrDefault(v => v.Name == "current");
        Assert.IsNotNull(current, "Expected the 'current' parameter among the locals");

        var evaluation = await session.EvaluateExpression("current + 100", frames[0].Id);
        Assert.AreEqual("int", evaluation.Type);
        Assert.AreEqual(int.Parse(current.Value) + 100, int.Parse(evaluation.Result));
    }

    [TestMethod]
    public async Task StepOver_FromBreakpoint_MovesToNextLineAndStopsAgain()
    {
        var breakpointLine = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");
        var stepLine = TestPaths.FindMarkerLine("STEP-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, breakpointLine);
        var state = WaitForStop(session);

        session.StepOver(state.StoppedThreadId!.Value);
        var afterStep = WaitForStop(session);

        Assert.AreEqual("step", afterStep.StopReason);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{stepLine}", afterStep.CurrentLocation);
    }

    [TestMethod]
    public async Task Continue_AfterBreakpoint_ResumesAndHitsBreakpointAgain()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        WaitForStop(session);

        Assert.IsTrue(session.Continue(), "Continue should resume a stopped process");

        var state = WaitForStop(session);
        Assert.AreEqual("breakpoint", state.StopReason);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{line}", state.CurrentLocation);
    }

    /// <summary>
    /// Regression: ManagedDebugger.SetBreakpoints has DAP replace semantics - it clears every
    /// breakpoint in the file before creating the ones passed in. Sending a single line per call
    /// therefore silently discarded every breakpoint previously set in the same file.
    /// </summary>
    [TestMethod]
    public async Task SetBreakpoint_SecondInSameFile_KeepsTheFirst()
    {
        var first = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");
        var second = TestPaths.FindMarkerLine("STEP-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var a = session.SetBreakpoint(TestPaths.TestAppSource, first);
        var b = session.SetBreakpoint(TestPaths.TestAppSource, second);

        Assert.IsTrue(a.Verified, $"First breakpoint not verified: {a.Message}");
        Assert.IsTrue(b.Verified, $"Second breakpoint not verified: {b.Message}");

        var listed = session.ListBreakpoints();
        Assert.AreEqual(2, listed.Count, "Both breakpoints in the same file should survive");
        CollectionAssert.AreEquivalent(
            new[] { first, second },
            listed.Select(x => x.Line).ToArray());
        Assert.IsTrue(listed.All(x => x.Verified));

        // ListBreakpoints reports this session's own bookkeeping, which cannot prove the
        // breakpoints are armed in the debugger. Stopping at both lines can.
        var stops = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            stops.Add(LineOf(WaitForStop(session).CurrentLocation));
            session.Continue();
        }

        CollectionAssert.AreEquivalent(
            new[] { first, second },
            stops,
            $"Expected to stop at both lines, stopped at: {string.Join(", ", stops)}");
    }

    private static int LineOf(string? location)
    {
        Assert.IsNotNull(location, "Stop reported no location");
        return int.Parse(location[(location.LastIndexOf(':') + 1)..]);
    }

    [TestMethod]
    public async Task RemoveBreakpoint_StopsTheProcessFromBreakingThere()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        var breakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line);
        WaitForStop(session);

        Assert.IsTrue(session.RemoveBreakpoint(breakpoint.Id));
        Assert.AreEqual(0, session.ListBreakpoints().Count);

        session.Continue();

        // The line is hit every iteration, so if the breakpoint were still armed the process
        // would stop again almost immediately instead of producing output.
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(ObservationWindow),
            "Process stopped again after its only breakpoint was removed");
        Assert.IsTrue(session.GetExecutionState().IsRunning);
    }

    [TestMethod]
    public async Task RemoveBreakpoint_LeavesTheOtherBreakpointsInTheFileArmed()
    {
        var first = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");
        var second = TestPaths.FindMarkerLine("STEP-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        var a = session.SetBreakpoint(TestPaths.TestAppSource, first);
        session.SetBreakpoint(TestPaths.TestAppSource, second);

        Assert.IsTrue(session.RemoveBreakpoint(a.Id));

        var listed = session.ListBreakpoints();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(second, listed[0].Line);
        Assert.IsTrue(listed[0].Verified, $"Remaining breakpoint lost its binding: {listed[0].Message}");

        var state = WaitForStop(session);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{second}", state.CurrentLocation);
    }

    [TestMethod]
    public async Task RemoveBreakpoint_UnknownId_ReturnsFalse()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        Assert.IsFalse(session.RemoveBreakpoint(4242));
    }

    [TestMethod]
    public async Task ListBreakpoints_WithNoneSet_IsEmpty()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        Assert.AreEqual(0, session.ListBreakpoints().Count);
    }

    /// <summary>
    /// Regression: continuing an already-running process threw CORDBG_E_SUPERFLOUS_CONTINUE
    /// straight out of COM instead of being reported as a no-op.
    /// </summary>
    [TestMethod]
    public async Task Continue_WhenAlreadyRunning_IsNoOpInsteadOfComFailure()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        Assert.IsTrue(session.GetExecutionState().IsRunning);
        Assert.IsFalse(session.Continue(), "Continue on a running process should report that nothing was resumed");
    }

    /// <summary>
    /// Regression: Detach only tore down local state and never called Disconnect, so ICorDebug kept
    /// the debuggee suspended for the rest of its life.
    /// </summary>
    [TestMethod]
    public async Task Detach_WhileStoppedAtBreakpoint_ReleasesTheDebuggee()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        WaitForStop(session);
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow));

        session.Detach();

        Assert.IsFalse(session.IsAttached);
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(ObservationWindow),
            "Debuggee stayed suspended after detach");
    }

    /// <summary>
    /// Regression: Dispose set _disposed and then called Detach, which guarded on _disposed and
    /// threw ObjectDisposedException on every session teardown.
    /// </summary>
    [TestMethod]
    public async Task Dispose_AfterAttach_DoesNotThrowAndReleasesTheDebuggee()
    {
        using var debuggee = DebuggeeProcess.Start();
        var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        session.Dispose();

        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(ObservationWindow),
            "Debuggee stayed suspended after the session was disposed");
    }
}
