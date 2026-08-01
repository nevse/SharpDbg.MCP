using System.Diagnostics;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;
using SharpDbg.MCP.Tools;

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
        Assert.HasCount(2, listed, "Both breakpoints in the same file should survive");
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

    /// <summary>
    /// Reads the debuggee's loop counter at the current stop, so tests can pin conditions to a
    /// value relative to where the process happens to be rather than guessing an absolute one.
    /// </summary>
    private static async Task<int> ReadCurrentAsync(DebugSession session, ExecutionState state)
    {
        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        var evaluation = await session.EvaluateExpression("current", frames[0].Id);
        return int.Parse(evaluation.Result);
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
        Assert.IsEmpty(session.ListBreakpoints());

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
        Assert.HasCount(1, listed);
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

        Assert.IsEmpty(session.ListBreakpoints());
    }

    [TestMethod]
    public async Task ConditionalBreakpoint_OnlyStopsWhenTheConditionHolds()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var start = await ReadCurrentAsync(session, WaitForStop(session));

        // Two iterations ahead, so the condition cannot already be satisfied
        var target = start + 2;
        var breakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line, $"current == {target}");
        Assert.IsTrue(breakpoint.Verified, $"Conditional breakpoint not verified: {breakpoint.Message}");
        Assert.AreEqual($"current == {target}", breakpoint.Condition);

        session.Continue();

        var stopped = WaitForStop(session);
        Assert.AreEqual("breakpoint", stopped.StopReason);
        Assert.AreEqual(
            target,
            await ReadCurrentAsync(session, stopped),
            "Stopped on an iteration where the condition was false");
    }

    [TestMethod]
    public async Task HitConditionBreakpoint_StopsOnTheRequestedHit()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var start = await ReadCurrentAsync(session, WaitForStop(session));

        // Re-sending the breakpoint recreates it, so its hit count restarts from zero here
        session.SetBreakpoint(TestPaths.TestAppSource, line, condition: null, hitCondition: "==3");
        session.Continue();

        var stopped = WaitForStop(session);
        Assert.AreEqual(
            start + 3,
            await ReadCurrentAsync(session, stopped),
            "Expected to stop on the third hit after the count was reset");
    }

    [TestMethod]
    public async Task SetBreakpoint_InvalidHitCondition_IsRejected()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        // The debugger treats an unparseable hit condition as never satisfied, so it has to be
        // rejected up front rather than producing a breakpoint that silently never fires.
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("every other"));
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("%0"));

        InputValidation.ValidateHitCondition(">=3");
        session.SetBreakpoint(TestPaths.TestAppSource, line, condition: null, hitCondition: ">=3");
        Assert.AreEqual(">=3", session.ListBreakpoints()[0].HitCondition);
    }

    [TestMethod]
    public async Task ExpandVariable_WalksIntoTheMembersOfAnObject()
    {
        var line = TestPaths.FindMarkerLine("EXPAND-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var state = WaitForStop(session);

        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        var variables = await session.GetVariables(frames[0].Id);

        var point = variables.SingleOrDefault(v => v.Name == "point");
        Assert.IsNotNull(point, $"Expected a 'point' local, got: {string.Join(", ", variables.Select(v => v.Name))}");
        Assert.IsGreaterThan(0, point.VariablesReference, "An object should be expandable");

        var members = await session.ExpandVariable(point.VariablesReference);
        var x = members.SingleOrDefault(m => m.Name == "X");
        var y = members.SingleOrDefault(m => m.Name == "Y");

        Assert.IsNotNull(x, $"Expected member X, got: {string.Join(", ", members.Select(m => m.Name))}");
        Assert.IsNotNull(y);

        // Point is built as (next, label.Length), so X must agree with the local it came from
        var next = variables.Single(v => v.Name == "next");
        Assert.AreEqual(next.Value, x.Value);
    }

    /// <summary>
    /// Pins a SharpDbg 0.1.7 defect. Expanding a member whose value needs function evaluation - a
    /// record's EqualityContract, which is a RuntimeType - leaves the variable manager holding a
    /// disposed handle. Every later Continue then fails with 0x80131C01 and the debuggee stays
    /// suspended for good; only detaching releases it. When this test starts failing the package
    /// has fixed it and the warning on ExpandVariable can go.
    /// </summary>
    [TestMethod]
    public async Task ExpandVariable_MemberNeedingEvaluation_PoisonsLaterContinue()
    {
        var line = TestPaths.FindMarkerLine("EXPAND-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var state = WaitForStop(session);

        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        var variables = await session.GetVariables(frames[0].Id);
        var point = variables.Single(v => v.Name == "point");

        var members = await session.ExpandVariable(point.VariablesReference);
        var needsEvaluation = members.Single(m => m.VariablesReference > 0);
        await session.ExpandVariable(needsEvaluation.VariablesReference);

        Exception? failure = null;
        try
        {
            session.Continue();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.IsNotNull(failure, "Continue unexpectedly succeeded after the poisoning expansion");
        StringAssert.Contains(failure.Message, "0x80131C01");
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow), "Debuggee should be stuck");

        // Detaching is the only way out, and it must still work
        session.Detach();
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(ObservationWindow),
            "Detach failed to release a debuggee stuck by the disposed handle");
    }

    /// <summary>
    /// Pins a SharpDbg 0.1.7 limitation: ManagedDebugger.Evaluate hardcodes variablesReference to
    /// 0 on every path, so an evaluated object cannot be expanded - only variables returned by
    /// GetVariables carry a usable reference. When this test starts failing the package has gained
    /// the capability, and ExpandVariable's description should stop excluding it.
    /// </summary>
    [TestMethod]
    public async Task EvaluateExpression_DoesNotYetYieldAnExpandableReference()
    {
        var line = TestPaths.FindMarkerLine("EXPAND-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var state = WaitForStop(session);

        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        var evaluated = await session.EvaluateExpression("point", frames[0].Id);

        Assert.AreEqual(0, evaluated.VariablesReference);
    }

    [TestMethod]
    public async Task ExpandVariable_UnknownReference_Throws()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.ExpandVariable(987654));
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
    /// Regression: a breakpoint set right after attaching can be answered unverified because the
    /// target module's symbols are not processed yet, and it binds a moment later on the
    /// module-load callback. SetBreakpoint waits for that, but the wait used to be bounded by the
    /// 30 second operation timeout, so a breakpoint that could never bind - a path the target does
    /// not contain - stalled the caller for the full 30 seconds before reporting it.
    /// </summary>
    [TestMethod]
    public async Task SetBreakpoint_OnAPathTheTargetDoesNotContain_ReportsUnverifiedWithinTheBindTimeout()
    {
        var bindTimeout = TimeSpan.FromMilliseconds(500);

        using var debuggee = DebuggeeProcess.Start();
        using var session = new DebugSession(
            1,
            new ServerConfiguration { BreakpointBindTimeoutMs = (int)bindTimeout.TotalMilliseconds });

        await session.Attach(debuggee.ProcessId);

        var elapsed = Stopwatch.StartNew();
        var result = session.SetBreakpoint(Path.Combine(Path.GetTempPath(), "NotPartOfTheApp.cs"), 3);
        elapsed.Stop();

        Assert.IsFalse(result.Verified, "A path the target does not contain cannot bind");
        Assert.IsNotNull(result.Message, "The caller needs to know why the breakpoint is unverified");

        // Bounded by the configured bind timeout rather than by the operation timeout
        Assert.IsLessThan(bindTimeout * 6, elapsed.Elapsed, "SetBreakpoint stalled well past the bind timeout");

        // And the wait did happen - a pending breakpoint gets its chance to bind
        Assert.IsGreaterThan(bindTimeout / 2, elapsed.Elapsed, "SetBreakpoint did not wait for the breakpoint to bind");
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
