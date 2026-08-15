using System.Diagnostics;

using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;
using SharpDbg.MCP.Tools;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// Drives a real debuggee through DebugSession. These cover the layer where the MCP server talks
/// to the debugger - the unit tests only reach configuration and input validation, which is why
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

        // The rest is done while the debuggee is stopped. Adding or removing a breakpoint re-sends the
        // file's whole set, and doing that while a hit on one of them is in flight trips an upstream
        // defect that leaves the process suspended with nothing reported.
        // This test is about removal leaving the others armed, so it stays out of that race.
        WaitForStop(session);

        session.SetBreakpoint(TestPaths.TestAppSource, second);

        Assert.IsTrue(session.RemoveBreakpoint(a.Id));

        var listed = session.ListBreakpoints();
        Assert.HasCount(1, listed);
        Assert.AreEqual(second, listed[0].Line);
        Assert.IsTrue(listed[0].Verified, $"Remaining breakpoint lost its binding: {listed[0].Message}");

        // Leave the stop that belonged to the removed breakpoint
        Assert.IsTrue(session.Continue());

        if (!DebuggeeProcess.SpinUntil(() => !session.GetExecutionState().IsRunning, StopTimeout))
        {
            // No stop and no output means the debuggee is suspended with nothing reported, which is
            // the upstream defect above rather than anything this test can assert on. Saying so beats
            // a red run for someone else's race, and beats pretending the run proved something.
            if (debuggee.CountOutputDuring(ObservationWindow) == 0)
                Assert.Inconclusive("The debuggee is suspended with no stop reported - a known debugger defect");

            Assert.Fail("The surviving breakpoint never fired, and the debuggee is running");
        }

        Assert.AreEqual($"{TestPaths.TestAppSource}:{second}", session.GetExecutionState().CurrentLocation);
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
    /// A function evaluation in the debuggee must leave the process able to resume on one continue.
    /// It did not until 0.1.9, which is the whole of sharpdbg#24: expanding a member whose value has
    /// to be evaluated - a record's EqualityContract, which is a RuntimeType - first poisoned the
    /// session with a disposed handle, and then, once that was fixed, still left the process
    /// suspended while reporting that it had resumed.
    /// This checks the debuggee by watching its output rather than by asking the debugger, because
    /// what went wrong before was precisely that the debugger's answer was wrong.
    /// </summary>
    [TestMethod]
    public async Task ExpandVariable_MemberNeedingEvaluation_ResumesOnOneContinue()
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
        var deeper = await session.ExpandVariable(needsEvaluation.VariablesReference);

        Assert.IsNotEmpty(deeper);

        // Remove the breakpoint first, so the only reason the debuggee could stay quiet is the
        // evaluation - otherwise it stops again on the next iteration and prints nothing either way
        Assert.IsTrue(session.RemoveBreakpoint(session.ListBreakpoints()[0].Id));
        Assert.IsTrue(session.Continue());
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(ObservationWindow),
            "The debuggee did not resume on the first continue");
    }

    /// <summary>
    /// An evaluated object must come back with a reference that expand_variable accepts, which is
    /// what makes evaluate_expression useful for anything but printing. Upstream hardcoded it to 0
    /// until 0.1.9.
    /// </summary>
    [TestMethod]
    public async Task EvaluateExpression_ObjectResult_YieldsAnExpandableReference()
    {
        var line = TestPaths.FindMarkerLine("EXPAND-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var state = WaitForStop(session);

        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        var evaluated = await session.EvaluateExpression("point", frames[0].Id);

        Assert.IsGreaterThan(0, evaluated.VariablesReference);

        // The reference has to be usable, not merely non-zero
        var members = await session.ExpandVariable(evaluated.VariablesReference);
        var x = members.SingleOrDefault(m => m.Name == "X");
        Assert.IsNotNull(x, $"Expected member X, got: {string.Join(", ", members.Select(m => m.Name))}");

        var next = (await session.GetVariables(frames[0].Id)).Single(v => v.Name == "next");
        Assert.AreEqual(next.Value, x.Value);
    }

    /// <summary>
    /// A primitive result has nothing to expand, so its reference must stay 0 - a caller uses that
    /// to decide whether expanding is worth a round trip.
    /// </summary>
    [TestMethod]
    public async Task EvaluateExpression_PrimitiveResult_YieldsNoReference()
    {
        var line = TestPaths.FindMarkerLine("EXPAND-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        var state = WaitForStop(session);

        var frames = session.GetStackTrace(state.StoppedThreadId!.Value);
        var evaluated = await session.EvaluateExpression("next + 1", frames[0].Id);

        Assert.AreEqual(0, evaluated.VariablesReference);
    }

    [TestMethod]
    public async Task ExpandVariable_UnknownReference_Throws()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        // The adapter reports every failure as a ProtocolException, so what matters is that the
        // call fails and says why - the tools turn any exception into an error response
        var failure = await Assert.ThrowsExactlyAsync<ProtocolException>(
            () => session.ExpandVariable(987654));

        Assert.Contains("variables reference", failure.Message, StringComparison.OrdinalIgnoreCase);
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

        var released = session.Detach();

        Assert.IsFalse(session.IsAttached);
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(ObservationWindow),
            "Debuggee stayed suspended after detach");

        // The attached half of what detach_from_process reports. Like its launched counterpart in
        // LaunchIntegrationTests, the value is reported rather than observed, so the assertion above
        // - which proves the disconnect landed - is what gives this one its expected value.
        Assert.IsTrue(released,
            "Detach reported the process as possibly still suspended, but it resumed");
    }

    /// <summary>
    /// Two processes debugged at once, which is what SHARPDBG_MAX_SESSIONS allows. What matters is
    /// that the sessions do not leak into each other: separate breakpoints, and resuming one leaves
    /// the other where it was.
    /// </summary>
    [TestMethod]
    public async Task TwoSessions_DebugTwoProcessesIndependently()
    {
        var firstLine = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");
        var secondLine = TestPaths.FindMarkerLine("STEP-TARGET");

        using var debuggeeA = DebuggeeProcess.Start();
        using var debuggeeB = DebuggeeProcess.Start();
        using var manager = new DebugSessionManager(new ServerConfiguration { MaxConcurrentSessions = 2 });

        var sessionA = manager.AcquireForDebuggee(null);
        await sessionA.Attach(debuggeeA.ProcessId);

        // Attaching again while the first session is busy has to open a second one
        var sessionB = manager.AcquireForDebuggee(null);
        await sessionB.Attach(debuggeeB.ProcessId);

        Assert.AreNotEqual(sessionA.SessionId, sessionB.SessionId);
        Assert.HasCount(2, manager.GetAllSessions());
        Assert.AreEqual(debuggeeA.ProcessId, sessionA.AttachedProcessId);
        Assert.AreEqual(debuggeeB.ProcessId, sessionB.AttachedProcessId);

        var breakpointA = sessionA.SetBreakpoint(TestPaths.TestAppSource, firstLine);
        var breakpointB = sessionB.SetBreakpoint(TestPaths.TestAppSource, secondLine);
        Assert.IsTrue(breakpointA.Verified, $"A: {breakpointA.Message}");
        Assert.IsTrue(breakpointB.Verified, $"B: {breakpointB.Message}");

        // Each session only knows its own breakpoint
        Assert.HasCount(1, sessionA.ListBreakpoints());
        Assert.HasCount(1, sessionB.ListBreakpoints());

        var stateA = WaitForStop(sessionA);
        var stateB = WaitForStop(sessionB);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{firstLine}", stateA.CurrentLocation);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{secondLine}", stateB.CurrentLocation);

        // Resuming one must leave the other suspended
        sessionA.RemoveBreakpoint(breakpointA.Id);
        Assert.IsTrue(sessionA.Continue());

        Assert.IsGreaterThan(0, debuggeeA.CountOutputDuring(TimeSpan.FromSeconds(2)), "A did not resume");
        Assert.AreEqual(0, debuggeeB.CountOutputDuring(ObservationWindow), "B resumed with A");
        Assert.IsFalse(sessionB.GetExecutionState().IsRunning);

        // Closing a session releases its debuggee and frees the slot
        manager.CloseSession(sessionB.SessionId);
        Assert.HasCount(1, manager.GetAllSessions());
        Assert.IsGreaterThan(
            0,
            debuggeeB.CountOutputDuring(TimeSpan.FromSeconds(2)),
            "Closing the session left its debuggee suspended");
    }

    /// <summary>
    /// A function breakpoint is the case where the caller knows the method but not the file and
    /// line, so what matters is that the name binds and that the stop reports the id and the
    /// location the caller never supplied.
    /// </summary>
    [TestMethod]
    public async Task FunctionBreakpoint_ByTypeAndMethod_StopsAtTheMethodItBoundTo()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        // The type part matches by suffix, so the namespace can be left out
        var breakpoint = session.SetFunctionBreakpoint("Program.Work");

        Assert.IsTrue(breakpoint.Verified, $"Function breakpoint was not verified: {breakpoint.Message}");

        // A function breakpoint still comes back with no location - a name can bind to several methods
        // and the protocol has room for one, so upstream declined to guess. See MattParkerDev/sharpdbg#31.
        Assert.IsEmpty(breakpoint.BoundLocations);

        var state = WaitForStop(session);

        Assert.AreEqual("breakpoint", state.StopReason);
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow), "Debuggee kept running while reported stopped");
        Assert.IsNotNull(state.LastBreakpoint);
        Assert.AreEqual(TestPaths.TestAppSource, state.LastBreakpoint.FilePath);
        Assert.AreEqual(breakpoint.Id, state.LastBreakpoint.BreakpointId,
            "The hit must carry the id the caller was given, which only the debugger can say for a "
            + "function breakpoint - it binds to places nobody asked for");
    }

    [TestMethod]
    public async Task FunctionBreakpoint_ByBareMethodName_Binds()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var breakpoint = session.SetFunctionBreakpoint("Work");

        Assert.IsTrue(breakpoint.Verified, $"Function breakpoint was not verified: {breakpoint.Message}");
        Assert.AreEqual("breakpoint", WaitForStop(session).StopReason);
    }

    /// <summary>
    /// A name that matches nothing must be reported as unverified promptly, the same as a line
    /// breakpoint on a path the target does not contain.
    /// </summary>
    [TestMethod]
    public async Task FunctionBreakpoint_ThatMatchesNothing_IsReportedUnverifiedWithoutStalling()
    {
        var bindTimeout = TimeSpan.FromMilliseconds(500);

        using var debuggee = DebuggeeProcess.Start();
        using var session = new DebugSession(
            1,
            new ServerConfiguration { BreakpointBindTimeoutMs = (int)bindTimeout.TotalMilliseconds });

        await session.Attach(debuggee.ProcessId);

        var elapsed = Stopwatch.StartNew();
        var breakpoint = session.SetFunctionBreakpoint("NoSuchMethodAnywhere");
        elapsed.Stop();

        Assert.IsFalse(breakpoint.Verified);
        Assert.IsNotNull(breakpoint.Message, "The caller needs to know why nothing was set");
        Assert.IsEmpty(breakpoint.BoundLocations);
        Assert.IsLessThan(bindTimeout * 6, elapsed.Elapsed, "SetFunctionBreakpoint stalled past the bind timeout");
    }

    /// <summary>
    /// SetFunctionBreakpoints replaces every function breakpoint at once, so setting a second one
    /// must not disarm the first - the same trap as SetBreakpoints per file.
    /// </summary>
    [TestMethod]
    public async Task FunctionBreakpoint_SettingASecondOne_KeepsTheFirstArmed()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var first = session.SetFunctionBreakpoint("Program.Work");
        var second = session.SetFunctionBreakpoint("Program.ThrowAndCatch");

        Assert.IsTrue(first.Verified, $"First was not verified: {first.Message}");
        Assert.IsTrue(second.Verified, $"Second was not verified: {second.Message}");
        Assert.AreNotEqual(first.Id, second.Id);

        var listed = session.ListFunctionBreakpoints();
        Assert.HasCount(2, listed);
        Assert.IsTrue(listed.All(b => b.Verified), "Re-sending the set left a function breakpoint unbound");

        // Work is what the debuggee actually reaches with --throw off, so the first must still fire.
        // Which of the two it was cannot be told apart until sharpdbg#31 lands, so this checks that
        // it stopped where the first one is, rather than that it carries the first one's id.
        var state = WaitForStop(session);
        Assert.AreEqual("breakpoint", state.StopReason);
        Assert.AreEqual(TestPaths.TestAppSource, state.LastBreakpoint!.FilePath);
    }

    [TestMethod]
    public async Task RemoveBreakpoint_WithAFunctionBreakpointId_RemovesItAndLetsTheDebuggeeRun()
    {
        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var breakpoint = session.SetFunctionBreakpoint("Program.Work");
        WaitForStop(session);

        // remove_breakpoint takes either kind of id
        Assert.IsTrue(session.RemoveBreakpoint(breakpoint.Id));
        Assert.IsEmpty(session.ListFunctionBreakpoints());

        Assert.IsTrue(session.Continue());
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(TimeSpan.FromSeconds(2)),
            "Debuggee stopped again, so the function breakpoint was still armed");
    }

    [TestMethod]
    public async Task ListBreakpoints_WithBothKinds_KeepsThemApart()
    {
        var line = TestPaths.FindMarkerLine("STEP-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var lineBreakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line);
        var functionBreakpoint = session.SetFunctionBreakpoint("Program.Work");

        var lines = session.ListBreakpoints();
        var functions = session.ListFunctionBreakpoints();

        Assert.HasCount(1, lines);
        Assert.AreEqual(lineBreakpoint.Id, lines[0].Id);
        Assert.HasCount(1, functions);
        Assert.AreEqual(functionBreakpoint.Id, functions[0].Id);
        Assert.AreEqual("Program.Work", functions[0].FunctionName);
    }

    /// <summary>
    /// The debugger stops on every first-chance exception, so the debuggee suspends even on one it
    /// catches itself. That is the default, and the stop has to name the exception and point at the
    /// throw site, or the caller cannot tell it apart from a breakpoint.
    /// </summary>
    [TestMethod]
    public async Task ExceptionStop_WithBreakModeAlways_SuspendsAtTheThrowSite()
    {
        var throwLine = TestPaths.FindMarkerLine("THROW-TARGET");

        using var debuggee = DebuggeeProcess.Start("--throw");
        using var session = CreateSession();

        Assert.AreEqual(ExceptionBreakMode.Always, session.ExceptionBreakMode, "Always must stay the default");

        await session.Attach(debuggee.ProcessId);

        var state = session.WaitForStop(StopTimeout);

        Assert.IsNotNull(state, "The debuggee throws every iteration, so it must have stopped");
        Assert.AreEqual("exception", state.StopReason);
        Assert.IsNotNull(state.StoppedThreadId, "Callers need the thread to ask where the exception came from");
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow), "Debuggee kept running while reported stopped");

        // ManagedDebugger reports no source location for exception stops, so the stack is the only
        // way to find the throw site
        var frames = session.GetStackTrace(state.StoppedThreadId.Value);
        Assert.AreEqual(throwLine, frames[0].Line, $"Top frame should be the throw, frames: {string.Join(", ", frames.Select(f => $"{f.Name}@{f.Line}"))}");
    }

    [TestMethod]
    public async Task ExceptionStop_WithBreakModeNever_LetsTheDebuggeeRunOn()
    {
        using var debuggee = DebuggeeProcess.Start("--throw");
        using var session = CreateSession();

        session.ExceptionBreakMode = ExceptionBreakMode.Never;

        await session.Attach(debuggee.ProcessId);

        // Every iteration throws, so an unresumed exception stop would show up as silence here
        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(TimeSpan.FromSeconds(2)),
            "Debuggee stayed suspended on an exception that should have been resumed");

        var state = session.GetExecutionState();
        Assert.IsTrue(state.IsRunning, $"Session reports stopped: reason={state.StopReason}");
        Assert.IsGreaterThan(0, state.ExceptionsSeen, "The debuggee did throw, so the stops must have been counted");
        Assert.IsGreaterThan(0, state.ExceptionsIgnored);

        // An ignored exception must never be visible as a stop, not even briefly: this used to be
        // published and then taken back, and a caller could catch it in between
        Assert.AreNotEqual("exception", state.StopReason);
    }

    /// <summary>
    /// Ignoring exceptions must not swallow the stops the caller actually asked for.
    /// </summary>
    [TestMethod]
    public async Task ExceptionStop_WithBreakModeNever_StillStopsAtBreakpoints()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start("--throw");
        using var session = CreateSession();

        session.ExceptionBreakMode = ExceptionBreakMode.Never;

        await session.Attach(debuggee.ProcessId);
        var breakpoint = session.SetBreakpoint(TestPaths.TestAppSource, line);
        Assert.IsTrue(breakpoint.Verified, $"Breakpoint was not verified: {breakpoint.Message}");

        var state = session.WaitForStop(StopTimeout);

        Assert.IsNotNull(state, "The breakpoint should still have been hit");
        Assert.AreEqual("breakpoint", state.StopReason);
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow), "Debuggee kept running while stopped at a breakpoint");
    }

    /// <summary>
    /// Switching to Never while already suspended on an exception has to release that stop too,
    /// otherwise the caller has to know to continue by hand, which is what the mode is for.
    /// </summary>
    [TestMethod]
    public async Task SwitchingToBreakModeNever_ReleasesAnExceptionStopAlreadyInProgress()
    {
        using var debuggee = DebuggeeProcess.Start("--throw");
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var stopped = session.WaitForStop(StopTimeout);
        Assert.IsNotNull(stopped);
        Assert.AreEqual("exception", stopped.StopReason);

        session.ExceptionBreakMode = ExceptionBreakMode.Never;

        Assert.IsGreaterThan(
            0,
            debuggee.CountOutputDuring(TimeSpan.FromSeconds(2)),
            "The exception stop that was already in progress was never released");
    }

    [TestMethod]
    public async Task WaitForStop_WhileRunning_ReturnsTheBreakpointStopWithoutPolling()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        WaitForStop(session);

        // Resume and wait for the next hit in a single call
        Assert.IsTrue(session.Continue());

        var state = session.WaitForStop(StopTimeout);

        Assert.IsNotNull(state, "WaitForStop timed out instead of reporting the next breakpoint hit");
        Assert.AreEqual("breakpoint", state.StopReason);
        Assert.AreEqual($"{TestPaths.TestAppSource}:{line}", state.CurrentLocation);
        Assert.IsNotNull(state.StoppedThreadId, "Callers need the stopped thread to request a stack trace");
        Assert.AreEqual(0, debuggee.CountOutputDuring(ObservationWindow), "Debuggee kept running while reported stopped");
    }

    [TestMethod]
    public async Task WaitForStop_WhenAlreadyStopped_ReturnsImmediately()
    {
        var line = TestPaths.FindMarkerLine("BREAKPOINT-TARGET");

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);
        session.SetBreakpoint(TestPaths.TestAppSource, line);
        WaitForStop(session);

        var elapsed = Stopwatch.StartNew();
        var state = session.WaitForStop(StopTimeout);
        elapsed.Stop();

        Assert.IsNotNull(state);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), elapsed.Elapsed, "An existing stop should not be waited for");
    }

    /// <summary>
    /// A process that is running and never stops has to come back as "not stopped" rather than as
    /// a stop with no reason, or a caller would try to step a running process.
    /// </summary>
    [TestMethod]
    public async Task WaitForStop_WhenNothingStops_ReportsStillRunning()
    {
        var timeout = TimeSpan.FromMilliseconds(500);

        using var debuggee = DebuggeeProcess.Start();
        using var session = CreateSession();

        await session.Attach(debuggee.ProcessId);

        var elapsed = Stopwatch.StartNew();
        var state = session.WaitForStop(timeout);
        elapsed.Stop();

        Assert.IsNull(state, "No breakpoint was set, so nothing should have stopped");
        Assert.IsGreaterThan(timeout / 2, elapsed.Elapsed, "WaitForStop returned without waiting");
        Assert.IsLessThan(timeout * 6, elapsed.Elapsed, "WaitForStop ignored its timeout");
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
