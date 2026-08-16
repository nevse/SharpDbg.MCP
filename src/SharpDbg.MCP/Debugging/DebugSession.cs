using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Logging;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Represents a debugging session that wraps ManagedDebugger
/// </summary>
public class DebugSession : IDisposable
{
    /// <summary>
    /// A launched program exists for the debugger before it exists as a process: everything that
    /// needs threads, frames or a resume has to wait for Start, while breakpoints must not.
    /// </summary>
    private enum SessionPhase
    {
        Idle,
        Prepared,
        Live
    }


    /// <summary>
    /// The reason ManagedDebugger reports for a first-chance exception stop
    /// </summary>
    private const string ExceptionStopReason = "exception";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    // macOS and Windows file systems are case-insensitive by default; Linux is not
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly int _sessionId;
    private readonly bool _justMyCode;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _evaluationTimeout;
    private readonly TimeSpan _breakpointBindTimeout;
    private readonly object _stateLock = new();
    // Held while the debugger is released, so a queued resume cannot call into it at the same time
    private readonly object _teardownLock = new();
    // Session-owned breakpoint ids. BreakpointManager reassigns its own ids every time a file's
    // breakpoints are re-sent, so they cannot be handed out as stable references.
    private readonly Dictionary<int, TrackedBreakpoint> _breakpoints = new();
    // Function breakpoints live in their own set upstream too: SetFunctionBreakpoints replaces only
    // those, and SetBreakpoints only the file's. Ids are still handed out from one counter, so a
    // caller can pass any id to RemoveBreakpoint without tracking which kind it was.
    private readonly Dictionary<int, TrackedFunctionBreakpoint> _functionBreakpoints = new();
    // The debuggee's own output, which only a launched process produces: SharpDbg redirects the
    // streams of what it starts, so without keeping it here nobody would ever see it
    private readonly Queue<OutputLine> _output = new();
    private const int MaxBufferedOutputLines = 1000;
    private int _nextBreakpointId = 1;
    private DapDebugger? _debugger;
    private SessionPhase _phase = SessionPhase.Idle;
    private string? _launchedProgram;
    private int? _attachedProcessId;
    private bool _disposed;
    private bool _isRunning;
    // Outcome of the last teardown, kept because Dispose cannot return one and close_session has
    // nothing else to report from
    private bool _releasedCleanly = true;
    private string? _currentLocation;
    // A DAP stop reports no location; it is fetched on demand, see ResolveStopLocation
    private bool _locationResolved = true;
    private string? _lastStopReason;
    private int? _lastStoppedThreadId;
    // Adapter ids the debugger attributes the current stop to, matched against TrackedBreakpoint.AdapterId
    private IReadOnlyList<int>? _lastHitAdapterIds;
    private BreakpointHitInfo? _lastBreakpoint;
    private ExceptionBreakMode _exceptionBreakMode = ExceptionBreakMode.Always;
    private int _exceptionsSeen;
    private int _exceptionsIgnored;

    public int SessionId => _sessionId;

    /// <summary>
    /// Whether first-chance exceptions suspend the debuggee (Always, the debugger's own behaviour)
    /// or are resumed by the session (Never). There is no mode for unhandled exceptions only:
    /// ManagedDebugger does not pass on the callback's event type, so a stop carries no way to tell
    /// whether the program is going to handle the exception.
    /// </summary>
    public ExceptionBreakMode ExceptionBreakMode
    {
        get
        {
            lock (_stateLock)
            {
                return _exceptionBreakMode;
            }
        }
        set
        {
            bool resumeNow;
            int threadId;

            lock (_stateLock)
            {
                _exceptionBreakMode = value;

                // Switching to Never while already stopped on an exception has to release that stop
                resumeNow = value == ExceptionBreakMode.Never
                    && !_isRunning
                    && _lastStopReason == ExceptionStopReason
                    && _phase == SessionPhase.Live;

                threadId = _lastStoppedThreadId ?? 0;

                if (resumeNow)
                {
                    // Unpublished for the same reason a new one would be: in this mode an exception
                    // stop is not something a caller should be able to see
                    _exceptionsIgnored++;
                    _isRunning = true;
                    ClearStopState();
                }
            }

            McpLogger.LogDebugSessionEvent(_sessionId, "ExceptionBreakMode", value.ToString());

            if (resumeNow)
                ResumeIgnoredException(threadId);
        }
    }

    /// <summary>
    /// Whether this session has a debuggee of its own, which includes a launched program that has
    /// not been started yet. The session manager reads it to find a session that is free.
    /// </summary>
    public bool IsAttached
    {
        get
        {
            lock (_stateLock)
            {
                return _phase != SessionPhase.Idle;
            }
        }
    }

    /// <summary>
    /// The process being debugged, or null for a launched one: SharpDbg sends no process event, so
    /// the pid of what it started is never reported to us.
    /// </summary>
    public int? AttachedProcessId
    {
        get
        {
            lock (_stateLock)
            {
                return _attachedProcessId;
            }
        }
    }

    /// <summary>
    /// The program this session launched, or null when it attached to an existing process
    /// </summary>
    public string? LaunchedProgram
    {
        get
        {
            lock (_stateLock)
            {
                return _launchedProgram;
            }
        }
    }

    /// <summary>
    /// Whether the last teardown did what it promised: suspended a launched program before
    /// terminating it, and got an answer to the disconnect. False means the debuggee's fate is
    /// unknown - a launched program may still be running, an attached one may still be suspended.
    /// Readable after Dispose on purpose: it records what happened rather than current state, and
    /// close_session has no other way to learn the outcome.
    /// </summary>
    public bool ReleasedCleanly
    {
        get
        {
            lock (_stateLock)
            {
                return _releasedCleanly;
            }
        }
    }

    /// <summary>
    /// Whether the launched program was ever started. False only in Prepared, where the program is
    /// described to the debugger and no process exists, so a teardown has nothing to kill.
    /// </summary>
    public bool HasStarted
    {
        get
        {
            lock (_stateLock)
            {
                return _phase != SessionPhase.Prepared;
            }
        }
    }

    public DebugSession(int sessionId, ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _sessionId = sessionId;
        _justMyCode = configuration.JustMyCode;
        _operationTimeout = TimeSpan.FromSeconds(configuration.OperationTimeoutSeconds);
        _evaluationTimeout = TimeSpan.FromMilliseconds(configuration.ExpressionEvaluationTimeoutMs);
        _breakpointBindTimeout = TimeSpan.FromMilliseconds(configuration.BreakpointBindTimeoutMs);
    }

    /// <summary>
    /// Attach to a process
    /// </summary>
    public async Task Attach(int processId)
    {
        DapDebugger debugger;

        lock (_stateLock)
        {
            debugger = TakeOverSession($"Process ID: {processId}", "Attaching");

            _attachedProcessId = processId;
            _phase = SessionPhase.Live;
            _isRunning = true;
        }

        try
        {
            await debugger.Attach(processId, _justMyCode, _operationTimeout);

            // The attach is handed to a fire-and-forget task inside the adapter, so the process is
            // still unusable when configurationDone returns. Threads stay empty until the process
            // exists, which makes it the cheapest signal that the attach has actually landed.
            if (!WaitFor(() => debugger.GetThreads().Count > 0, _operationTimeout))
                throw new TimeoutException($"Timed out waiting to attach to process {processId}");

            McpLogger.LogDebugSessionEvent(_sessionId, "Attached", $"Successfully attached to process {processId}");
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "Attach", ex);
            DetachCore();
            throw;
        }
    }

    /// <summary>
    /// Prepare a program for debugging without running it. Breakpoints set afterwards are in place
    /// before the first line executes, which is what makes startup debuggable; Start runs it.
    /// </summary>
    public async Task Launch(
        string program,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        DapDebugger debugger;

        lock (_stateLock)
        {
            debugger = TakeOverSession(program, "Launching");

            _launchedProgram = program;
            _phase = SessionPhase.Prepared;
            _isRunning = false;
        }

        try
        {
            await debugger.Launch(
                program,
                arguments ?? [],
                workingDirectory ?? Path.GetDirectoryName(program) ?? Environment.CurrentDirectory,
                environment ?? new Dictionary<string, string>(),
                _justMyCode,
                _operationTimeout);

            McpLogger.LogDebugSessionEvent(_sessionId, "Launched", $"{program}, not started yet");
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "Launch", ex);
            DetachCore();
            throw;
        }
    }

    /// <summary>
    /// Start the program prepared by Launch. The debuggee can hit a breakpoint before this returns,
    /// so the session is marked running first and a stop that arrives meanwhile wins.
    /// </summary>
    public void Start()
    {
        DapDebugger debugger;

        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (_phase != SessionPhase.Prepared)
                throw new InvalidOperationException(_phase == SessionPhase.Live
                    ? "The program is already running"
                    : "Nothing has been launched in this session");

            debugger = _debugger ?? throw new InvalidOperationException("Debugger not initialized");

            _phase = SessionPhase.Live;
            _isRunning = true;
        }

        try
        {
            debugger.Start(_operationTimeout);
            McpLogger.LogDebugSessionEvent(_sessionId, "Started", _launchedProgram ?? string.Empty);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "Start", ex);

            // Read before the teardown, which clears it. This is the failure where a launched
            // program is most likely to survive, and also the one whose session is gone by the time
            // anything could be asked about it, so the warning has to travel with the exception.
            string? program;

            lock (_stateLock)
                program = _launchedProgram;

            // Tear down rather than rolling back to Prepared. configurationDone is what creates the
            // process, so a failure here can leave one running, and the teardown reads the phase to
            // decide whether to pause before terminating. Rolling back first tells it there is
            // nothing to pause, and the terminate then fails against a running debuggee.
            if (DetachCore() || program == null)
                throw;

            // Hedged on purpose. configurationDone both creates the process and attaches to it, and a
            // failure does not say which half it got to, so whether there is a process at all is
            // exactly what is unknown here.
            throw new InvalidOperationException(
                $"{ex.Message} The debugger never confirmed releasing {program}, so if it was "
                + "started it may still be running.", ex);
        }
    }

    /// <summary>
    /// Everything a fresh debuggee needs of the session, under the caller's lock: a debugger wired
    /// to the handlers, and the leftovers of whatever was here before cleared away.
    /// </summary>
    private DapDebugger TakeOverSession(string what, string logEvent)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSession));

        if (_phase != SessionPhase.Idle)
            throw new InvalidOperationException("This session already has a debuggee");

        McpLogger.LogDebugSessionEvent(_sessionId, logEvent, what);

        var debugger = new DapDebugger(LogMessage);

        debugger.OnStopped += OnDebuggerStopped;
        debugger.OnContinued += OnDebuggerContinued;
        debugger.OnExited += OnDebuggerExited;
        debugger.OnOutput += OnDebuggerOutput;
        debugger.OnBreakpointChanged += OnDebuggerBreakpointChanged;

        _debugger = debugger;
        _lastBreakpoint = null;
        _exceptionsSeen = 0;
        _exceptionsIgnored = 0;
        _output.Clear();
        ClearStopState();

        return debugger;
    }

    private void OnDebuggerStopped(int threadId, string reason, IReadOnlyList<int>? hitBreakpointIds)
    {
        bool ignoring;

        lock (_stateLock)
        {
            if (reason == ExceptionStopReason)
                _exceptionsSeen++;

            // An ignored exception is never published as a stop. Reporting it and taking it back a
            // moment later would let a caller see - and act on - a stop the mode promised to hide.
            ignoring = reason == ExceptionStopReason
                && _exceptionBreakMode == ExceptionBreakMode.Never
                && _phase == SessionPhase.Live;

            if (ignoring)
            {
                _exceptionsIgnored++;
            }
            else
            {
                _isRunning = false;
                _lastStoppedThreadId = threadId;
                _lastStopReason = reason;
                _lastHitAdapterIds = hitBreakpointIds;

                // A DAP stop carries no location, and this is the protocol's reader thread, which is
                // also what reads request responses - asking for the stack here would deadlock. The
                // location is fetched by the first caller that wants it.
                _locationResolved = false;
            }
        }

        McpLogger.LogDebugSessionEvent(_sessionId, ignoring ? "ExceptionIgnored" : "Stopped",
            $"Thread {threadId}: {reason}");
        LogMessage($"{(ignoring ? "Ignored exception" : "Stopped")} on thread {threadId}: {reason}");

        if (ignoring)
            ResumeIgnoredException(threadId);
    }

    /// <summary>
    /// The debugger stops on every first-chance exception, including ones the program catches
    /// itself, and offers no way to filter them, so a program that uses exceptions routinely
    /// suspends on each one. In Never mode the session resumes them itself.
    /// The resume is queued rather than run here: this is the debugger's callback thread, and
    /// continuing inline would nest a stop inside a stop for as long as the exceptions keep coming.
    /// It goes straight to the debugger rather than through Continue, because the session never
    /// published this stop - as far as anything outside is concerned the process never stopped.
    /// </summary>
    private void ResumeIgnoredException(int threadId)
    {
        _ = Task.Run(() =>
        {
            // The resume must not overlap a teardown: Disconnect while a Continue is in flight
            // reaches ICorDebug on a process that is being released.
            lock (_teardownLock)
            {
                try
                {
                    DapDebugger? debugger;

                    lock (_stateLock)
                        debugger = _phase == SessionPhase.Live ? _debugger : null;

                    debugger?.Continue(threadId);
                }
                catch (Exception ex)
                {
                    // The process really is suspended now, so say so rather than leaving the session
                    // claiming it runs - that is the one thing worse than an unwanted stop
                    lock (_stateLock)
                    {
                        if (_phase == SessionPhase.Live)
                        {
                            _isRunning = false;
                            _lastStoppedThreadId = threadId;
                            _lastStopReason = ExceptionStopReason;
                        }
                    }

                    McpLogger.LogDebugSessionEvent(_sessionId, "ExceptionIgnored",
                        $"Could not resume thread {threadId}, reporting the stop instead: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Fills in where the debuggee stopped, by asking for the top of the stack. A DAP stop event
    /// carries only a thread and a reason, so this is the location's only source; it is done here,
    /// on a caller's thread, because it cannot be done on the one the event arrived on.
    /// </summary>
    private void ResolveStopLocation()
    {
        int threadId;
        string? reason;

        lock (_stateLock)
        {
            if (_locationResolved || _isRunning || _phase != SessionPhase.Live || _lastStoppedThreadId is null)
                return;

            threadId = _lastStoppedThreadId.Value;
            reason = _lastStopReason;
        }

        string? filePath = null;
        var line = 0;

        try
        {
            var top = _debugger?.GetStackTrace(threadId).FirstOrDefault();

            if (top?.Source is not null)
            {
                filePath = top.Source;
                line = top.Line;
            }
        }
        catch (Exception ex)
        {
            // A stop with no readable stack is still a stop; report it without a location rather
            // than failing the call that asked for the state
            McpLogger.LogDebugSessionEvent(_sessionId, "Stopped",
                $"Could not read the location on thread {threadId}: {ex.Message}");
        }

        lock (_stateLock)
        {
            // Resumed while we were asking - whatever we found describes a stop that is over
            if (_isRunning || _lastStoppedThreadId != threadId)
                return;

            _locationResolved = true;

            if (filePath is null)
                return;

            _currentLocation = $"{filePath}:{line}";

            if (reason == "breakpoint")
            {
                // The stop reports the line the PDB resolved to, which is not necessarily the
                // line that was requested, and for a function breakpoint was never requested.
                _lastBreakpoint = new BreakpointHitInfo(
                    FindIdByAdapterIds(_lastHitAdapterIds) ?? FindIdByLocation(filePath, line) ?? 0,
                    filePath, line, threadId);
            }
        }
    }

    private void OnDebuggerContinued(int threadId)
    {
        lock (_stateLock)
        {
            _isRunning = true;
            ClearStopState();
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Continued", $"Thread {threadId}");
        LogMessage($"Continued on thread {threadId}");
    }

    private void OnDebuggerExited()
    {
        lock (_stateLock)
        {
            _isRunning = false;
            ClearStopState();

            // A dead process is not running, so it satisfies WaitForStop. Naming the reason keeps
            // that from looking like a stop the caller can step or continue from.
            _lastStopReason = "exited";
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Exited", "Process terminated");
        LogMessage("Process exited");
    }

    private void OnDebuggerOutput(string message, bool isError)
    {
        McpLogger.LogDebug("Session {SessionId} | Output: {Message}", _sessionId, message);
        LogMessage($"Output{(isError ? " (stderr)" : string.Empty)}: {message}");

        lock (_stateLock)
        {
            _output.Enqueue(new OutputLine(message.TrimEnd('\r', '\n'), isError));

            while (_output.Count > MaxBufferedOutputLines)
                _output.Dequeue();
        }
    }

    /// <summary>
    /// What the debuggee has written so far, oldest first. A launched program has its streams
    /// redirected into the debug adapter, so this is the only place its output can be read.
    /// </summary>
    public IReadOnlyList<OutputLine> ReadOutput(int maxLines)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            return maxLines >= _output.Count
                ? _output.ToList()
                : _output.Skip(_output.Count - maxLines).ToList();
        }
    }

    /// <summary>
    /// A breakpoint bound, or failed to. The adapter identifies it by its own id, which is why every
    /// re-send records the ids it came back with: matching on location cannot tell two breakpoints on
    /// one line apart, and a function breakpoint has no location to match on at all.
    /// </summary>
    private void OnDebuggerBreakpointChanged(AppliedBreakpoint breakpoint)
    {
        string describedAs;

        lock (_stateLock)
        {
            var tracked = _breakpoints.Values.FirstOrDefault(b => b.AdapterId == breakpoint.Id);

            if (tracked != null)
            {
                ApplyDebuggerState(tracked, breakpoint);
                describedAs = $"{tracked.FilePath}:{tracked.Line}";
            }
            else
            {
                var trackedFunction =
                    _functionBreakpoints.Values.FirstOrDefault(b => b.AdapterId == breakpoint.Id);

                if (trackedFunction == null)
                    return;

                ApplyDebuggerState(trackedFunction, breakpoint);
                describedAs = trackedFunction.FunctionName;
            }
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "BreakpointChanged",
            $"{describedAs} (verified={breakpoint.Verified})");
        LogMessage($"Breakpoint changed: {describedAs} (verified={breakpoint.Verified})");
    }

    /// <summary>
    /// Get current execution state
    /// </summary>
    public ExecutionState GetExecutionState()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));
        }

        ResolveStopLocation();

        lock (_stateLock)
        {
            return new ExecutionState(
                _phase == SessionPhase.Live && _isRunning,
                _phase != SessionPhase.Idle,
                _attachedProcessId,
                _currentLocation,
                _lastBreakpoint,
                _lastStoppedThreadId,
                _lastStopReason,
                _exceptionsSeen,
                _exceptionsIgnored,
                _launchedProgram,
                _phase != SessionPhase.Prepared);
        }
    }

    /// <summary>
    /// Set a breakpoint at a specific file and line, or update the conditions of an existing one.
    /// Note that re-sending a file's breakpoints resets their hit counts, because
    /// ManagedDebugger.SetBreakpoints recreates every breakpoint in the file.
    /// </summary>
    public BreakpointResult SetBreakpoint(
        string filePath,
        int line,
        string? condition = null,
        string? hitCondition = null)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "SetBreakpoint", $"{filePath}:{line}");

        int id;
        var isNew = false;
        string? previousCondition = null;
        string? previousHitCondition = null;

        lock (_stateLock)
        {
            var existing = FindByLocation(filePath, line);

            if (existing != null)
            {
                id = existing.Id;
                previousCondition = existing.Condition;
                previousHitCondition = existing.HitCondition;
                existing.Condition = condition;
                existing.HitCondition = hitCondition;
            }
            else
            {
                isNew = true;
                id = _nextBreakpointId++;
                _breakpoints[id] = new TrackedBreakpoint(id, filePath, line)
                {
                    Condition = condition,
                    HitCondition = hitCondition
                };
            }
        }

        try
        {
            ReapplyFile(debugger, filePath);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "SetBreakpoint", ex);

            lock (_stateLock)
            {
                if (isNew)
                {
                    _breakpoints.Remove(id);
                }
                else if (_breakpoints.TryGetValue(id, out var reverted))
                {
                    reverted.Condition = previousCondition;
                    reverted.HitCondition = previousHitCondition;
                }
            }

            throw;
        }

        // A breakpoint created before the target module's symbols have loaded comes back
        // unverified and binds later, on the module-load callback. Wait for that instead of
        // reporting a pending breakpoint that is about to become active.
        var result = WaitForVerification(id);

        McpLogger.LogDebugSessionEvent(_sessionId, "BreakpointSet",
            $"ID {result.Id} at {result.FilePath}:{result.Line} (verified={result.Verified})");

        return result;
    }

    /// <summary>
    /// Set a breakpoint on every method matching a name, or update the conditions of an existing
    /// one. Useful when the method is known but the file and line are not. Note that re-sending the
    /// function breakpoints resets their hit counts, because ManagedDebugger.SetFunctionBreakpoints
    /// recreates all of them.
    /// </summary>
    public FunctionBreakpointResult SetFunctionBreakpoint(
        string functionName,
        string? condition = null,
        string? hitCondition = null)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "SetFunctionBreakpoint", functionName);

        int id;
        var isNew = false;
        string? previousCondition = null;
        string? previousHitCondition = null;

        lock (_stateLock)
        {
            var existing = FindByFunctionName(functionName);

            if (existing != null)
            {
                id = existing.Id;
                previousCondition = existing.Condition;
                previousHitCondition = existing.HitCondition;
                existing.Condition = condition;
                existing.HitCondition = hitCondition;
            }
            else
            {
                isNew = true;
                id = _nextBreakpointId++;
                _functionBreakpoints[id] = new TrackedFunctionBreakpoint(id, functionName)
                {
                    Condition = condition,
                    HitCondition = hitCondition
                };
            }
        }

        try
        {
            ReapplyFunctionBreakpoints(debugger);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "SetFunctionBreakpoint", ex);

            lock (_stateLock)
            {
                if (isNew)
                {
                    _functionBreakpoints.Remove(id);
                }
                else if (_functionBreakpoints.TryGetValue(id, out var reverted))
                {
                    reverted.Condition = previousCondition;
                    reverted.HitCondition = previousHitCondition;
                }
            }

            throw;
        }

        var result = WaitForBinding(
            () =>
            {
                lock (_stateLock)
                    return _functionBreakpoints.TryGetValue(id, out var tracked) ? tracked.ToResult() : null;
            },
            r => r.Verified);

        McpLogger.LogDebugSessionEvent(_sessionId, "FunctionBreakpointSet",
            $"ID {result.Id} on {result.FunctionName} (verified={result.Verified}, " +
            $"bound to {result.BoundLocations.Count} location(s))");

        return result;
    }

    /// <summary>
    /// Remove a breakpoint by its session id, of either kind. Returns false when no such breakpoint
    /// is set.
    /// </summary>
    public bool RemoveBreakpoint(int breakpointId)
    {
        var debugger = RequireDebugger();

        TrackedBreakpoint? removed;

        lock (_stateLock)
        {
            if (!_breakpoints.Remove(breakpointId, out removed))
                return RemoveFunctionBreakpoint(debugger, breakpointId);
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "RemoveBreakpoint",
            $"ID {breakpointId} at {removed!.FilePath}:{removed.Line}");

        try
        {
            ReapplyFile(debugger, removed.FilePath);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "RemoveBreakpoint", ex);

            lock (_stateLock)
            {
                _breakpoints[removed.Id] = removed;
            }

            throw;
        }

        return true;
    }

    private bool RemoveFunctionBreakpoint(DapDebugger debugger, int breakpointId)
    {
        TrackedFunctionBreakpoint? removed;

        lock (_stateLock)
        {
            if (!_functionBreakpoints.Remove(breakpointId, out removed))
                return false;
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "RemoveBreakpoint",
            $"ID {breakpointId} on {removed!.FunctionName}");

        try
        {
            ReapplyFunctionBreakpoints(debugger);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "RemoveBreakpoint", ex);

            lock (_stateLock)
            {
                _functionBreakpoints[removed.Id] = removed;
            }

            throw;
        }

        return true;
    }

    /// <summary>
    /// All file and line breakpoints currently set in this session
    /// </summary>
    public List<BreakpointResult> ListBreakpoints()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            return _breakpoints.Values
                .OrderBy(b => b.FilePath, PathComparer)
                .ThenBy(b => b.Line)
                .Select(b => b.ToResult())
                .ToList();
        }
    }

    /// <summary>
    /// All function breakpoints currently set in this session
    /// </summary>
    public List<FunctionBreakpointResult> ListFunctionBreakpoints()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            return _functionBreakpoints.Values
                .OrderBy(b => b.Id)
                .Select(b => b.ToResult())
                .ToList();
        }
    }

    /// <summary>
    /// Re-sends every breakpoint tracked for a file.
    /// ManagedDebugger.SetBreakpoints replaces the file's entire set, so sending a single line
    /// would silently drop every other breakpoint in that file.
    /// </summary>
    private void ReapplyFile(DapDebugger debugger, string filePath)
    {
        List<TrackedBreakpoint> forFile;

        lock (_stateLock)
        {
            forFile = _breakpoints.Values
                .Where(b => PathComparer.Equals(b.FilePath, filePath))
                .OrderBy(b => b.Line)
                .ToList();
        }

        var requests = forFile
            .Select(b => new SourceBreakpointRequest(b.Line, b.Condition, b.HitCondition))
            .ToArray();

        var applied = RetryWhileModulesLoad(() => debugger.SetBreakpoints(filePath, requests));

        // SetBreakpoints preserves request order, so results line up with forFile
        lock (_stateLock)
        {
            for (var i = 0; i < forFile.Count && i < applied.Count; i++)
                ApplyDebuggerState(forFile[i], applied[i]);
        }
    }

    /// <summary>
    /// Re-sends every function breakpoint.
    /// ManagedDebugger.SetFunctionBreakpoints replaces all of them at once, so sending a single one
    /// would silently drop the rest.
    /// </summary>
    private void ReapplyFunctionBreakpoints(DapDebugger debugger)
    {
        List<TrackedFunctionBreakpoint> all;

        lock (_stateLock)
        {
            all = _functionBreakpoints.Values.OrderBy(b => b.Id).ToList();
        }

        var requests = all
            .Select(b => new FunctionBreakpointRequest(b.FunctionName, b.Condition, b.HitCondition))
            .ToArray();

        var applied = RetryWhileModulesLoad(() => debugger.SetFunctionBreakpoints(requests));

        // SetFunctionBreakpoints preserves request order, so results line up with all
        lock (_stateLock)
        {
            for (var i = 0; i < all.Count && i < applied.Count; i++)
                ApplyDebuggerState(all[i], applied[i]);
        }
    }

    /// <summary>
    /// Retries a breakpoint call that lost a race with module loading.
    /// SharpDbg keeps its modules in a plain Dictionary that the module-load callback writes to on
    /// the debugger's own thread, while binding enumerates it on ours, so a call made while the
    /// debuggee is still loading assemblies can fail with "Collection was modified" on older
    /// versions. Retrying is safe because both requests replace a whole set rather than adding to
    /// one, so a call that half-finished leaves nothing behind to duplicate.
    /// Over DAP every failure arrives as a ProtocolException, so there is no type to tell that race
    /// apart from a real failure. Retrying them all is acceptable because these two requests are
    /// idempotent; the retries stay few and short, and whatever survives them reaches the caller.
    /// </summary>
    private static List<AppliedBreakpoint> RetryWhileModulesLoad(
        Func<List<AppliedBreakpoint>> apply)
    {
        const int MaxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return apply();
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is InvalidOperationException or ProtocolException)
            {
                Thread.Sleep(PollInterval);
            }
        }
    }

    /// <summary>
    /// Waits for a pending breakpoint to bind on the module-load callback, which is what makes a
    /// breakpoint set immediately after attaching come back verified. The wait is deliberately
    /// short: a breakpoint that can never bind - a path the target does not contain, or a method
    /// name that matches nothing, most often a typo - has no event coming and costs the full
    /// timeout before it is reported.
    /// </summary>
    private TResult WaitForBinding<TResult>(Func<TResult?> snapshot, Func<TResult, bool> isVerified)
        where TResult : class
    {
        TResult? current;

        bool beforeTheProgramRuns;

        lock (_stateLock)
            beforeTheProgramRuns = _phase == SessionPhase.Prepared;

        // Nothing can bind before the program is started - no modules are loaded, and the debuggee
        // does not exist. Such a breakpoint stays pending on purpose and binds during the launch.
        if (beforeTheProgramRuns)
        {
            current = snapshot();

            return current ?? throw new InvalidOperationException("Breakpoint disappeared while being set");
        }

        current = null;

        WaitFor(() =>
        {
            current = snapshot();

            // Removed while being set: there is nothing left to wait for
            return current == null || isVerified(current);
        }, _breakpointBindTimeout);

        return current ?? throw new InvalidOperationException("Breakpoint disappeared while being set");
    }

    private BreakpointResult WaitForVerification(int breakpointId)
    {
        return WaitForBinding(
            () =>
            {
                lock (_stateLock)
                    return _breakpoints.TryGetValue(breakpointId, out var tracked) ? tracked.ToResult() : null;
            },
            r => r.Verified);
    }

    /// <summary>
    /// Locates a tracked breakpoint by the line that was requested or the line it bound to
    /// </summary>
    private TrackedBreakpoint? FindByLocation(string filePath, int line)
    {
        return _breakpoints.Values.FirstOrDefault(b =>
            PathComparer.Equals(b.FilePath, filePath) && (b.Line == line || b.ResolvedLine == line));
    }

    private TrackedFunctionBreakpoint? FindByFunctionName(string functionName)
    {
        return _functionBreakpoints.Values.FirstOrDefault(b =>
            string.Equals(b.FunctionName, functionName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The id of whichever breakpoint covers a location, so a hit can be reported with the id the
    /// caller was given. A function breakpoint is only ever known by where it bound.
    /// </summary>
    private int? FindIdByLocation(string filePath, int line)
    {
        var tracked = FindByLocation(filePath, line);

        if (tracked != null)
            return tracked.Id;

        return _functionBreakpoints.Values
            .FirstOrDefault(b => b.BoundLocations.Any(
                l => PathComparer.Equals(l.FilePath, filePath) && l.Line == line))
            ?.Id;
    }

    /// <summary>
    /// The id of whichever breakpoint the debugger says the stop belongs to. This is the only way to
    /// attribute a function breakpoint hit: it binds to places nobody asked for, so matching by
    /// location cannot find it. Empty until SharpDbg 0.1.12, which is why the location match stays as
    /// a fallback.
    /// </summary>
    private int? FindIdByAdapterIds(IReadOnlyList<int>? adapterIds)
    {
        if (adapterIds is null || adapterIds.Count == 0)
            return null;

        foreach (var adapterId in adapterIds)
        {
            var tracked = _breakpoints.Values.FirstOrDefault(b => b.AdapterId == adapterId);

            if (tracked != null)
                return tracked.Id;

            var trackedFunction = _functionBreakpoints.Values.FirstOrDefault(b => b.AdapterId == adapterId);

            if (trackedFunction != null)
                return trackedFunction.Id;
        }

        return null;
    }

    private static void ApplyDebuggerState(TrackedBreakpoint tracked, AppliedBreakpoint info)
    {
        tracked.AdapterId = info.Id;
        tracked.Verified = info.Verified;
        tracked.Message = info.Message;
        tracked.ResolvedLine = info.Line;
    }

    /// <summary>
    /// A function breakpoint comes back over DAP with no location at all - upstream nulls Line and
    /// Source for them, in both the response and the event - so BoundLocations can only be filled
    /// from where it turns out to stop. Asked for upstream as MattParkerDev/sharpdbg#31.
    /// </summary>
    private static void ApplyDebuggerState(TrackedFunctionBreakpoint tracked, AppliedBreakpoint info)
    {
        tracked.AdapterId = info.Id;
        tracked.Verified = info.Verified;
        tracked.Message = info.Message;

        if (info.SourcePath is not null && info.Line is not null)
        {
            tracked.BoundLocations = [new BoundLocation(info.SourcePath, info.Line.Value)];
        }
    }

    /// <summary>
    /// Get stack trace for a specific thread
    /// </summary>
    public List<StackFrameInfo> GetStackTrace(int threadId)
    {
        return RequireLiveDebugger().GetStackTrace(threadId);
    }

    /// <summary>
    /// Get all threads in the process
    /// </summary>
    public List<ThreadInfo> GetThreads()
    {
        var threads = RequireLiveDebugger().GetThreads();
        return threads.Select(t => new ThreadInfo(t.Id, t.Name)).ToList();
    }

    /// <summary>
    /// Get variables for a stack frame by frame ID.
    /// ManagedDebugger exposes a single "Locals" scope per frame which already covers the current
    /// exception, the arguments and the locals.
    /// </summary>
    public async Task<List<VariableInfo>> GetVariables(int frameId)
    {
        var debugger = RequireLiveDebugger();

        // Call async methods outside the lock to avoid deadlocks
        return await Task.Run(() => debugger.GetFrameVariables(frameId));
    }

    /// <summary>
    /// Expand a variables reference into its members. References come from GetVariables or
    /// EvaluateExpression and are invalidated as soon as the process resumes.
    /// </summary>
    public async Task<List<VariableInfo>> ExpandVariable(int variablesReference)
    {
        var debugger = RequireLiveDebugger();

        // Off the caller's thread: the request blocks until the adapter answers
        return await Task.Run(() => debugger.GetVariables(variablesReference));
    }

    /// <summary>
    /// Continue execution until next breakpoint or exit.
    /// Returns false when the process was already running and nothing needed resuming.
    /// </summary>
    public bool Continue()
    {
        var debugger = RequireLiveDebugger();

        // Continuing an already-running process throws CORDBG_E_SUPERFLOUS_CONTINUE out of COM.
        if (!TryBeginResume())
        {
            McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Already running - ignored");
            return false;
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Resuming execution");
        Resume(() => debugger.Continue(StoppedThreadOrFirst(debugger)));
        return true;
    }

    /// <summary>
    /// Blocks until the debuggee stops - a breakpoint hit, a completed step, a pause, an exception
    /// or the process exiting - and returns the state it stopped in, or null if it is still running
    /// when the timeout expires. Callers otherwise have to poll GetExecutionState in a loop, which
    /// costs an MCP client a round trip per poll.
    /// </summary>
    public ExecutionState? WaitForStop(TimeSpan timeout)
    {
        RequireLiveDebugger();

        var stopped = WaitFor(() =>
        {
            lock (_stateLock)
            {
                return !_isRunning;
            }
        }, timeout);

        var state = GetExecutionState();

        McpLogger.LogDebugSessionEvent(_sessionId, "WaitForStop",
            stopped ? $"Stopped: {state.StopReason ?? "unknown"}" : $"Still running after {timeout}");

        return stopped ? state : null;
    }

    /// <summary>
    /// Breaks into the debugger. Returns whether the adapter confirmed the pause within the
    /// operation timeout; false means unconfirmed rather than failed, and the pause may still land.
    ///
    /// This used to wait forever, because the stop was recorded after the wait and so a bound would
    /// have skipped recording it - leaving the session claiming the program runs while it was in
    /// fact suspended. Recording it from the adapter's own confirmation instead removes that reason:
    /// whenever the pause lands, the session learns about it, whether or not anyone is still waiting.
    /// </summary>
    public bool Pause()
    {
        var debugger = RequireLiveDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "Pause", "Breaking execution");

        // One deadline covers both requests, so bounding this costs one operation timeout rather
        // than two. The thread lookup has to be inside it: when the debuggee is running there is no
        // retained stopped thread, so a thread has to be asked for before a pause can name one, and
        // that request had no bound of its own. Bounding only the pause would have left the ordinary
        // case hanging before the pause was even sent.
        var deadline = DateTime.UtcNow + _operationTimeout;

        if (StoppedThreadOrFirst(debugger, _operationTimeout) is not { } threadId)
            return false;

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return false;

        // Stop() is synchronous and raises no stop event of its own, so this is the only thing that
        // records the stop.
        return debugger.TryPause(threadId, remaining, MarkPaused);

        void MarkPaused()
        {
            lock (_stateLock)
            {
                _isRunning = false;
                _lastStopReason = "pause";
            }
        }
    }

    /// <summary>
    /// Step over (execute current line and stop at next line in same method)
    /// </summary>
    public void StepOver(int threadId)
    {
        var debugger = RequireLiveDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepOver", $"Thread {threadId}");
        BeginStep();
        Resume(() => debugger.StepOver(threadId));
    }

    /// <summary>
    /// Step into (execute current line and stop at first line of called method)
    /// </summary>
    public void StepInto(int threadId)
    {
        var debugger = RequireLiveDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepInto", $"Thread {threadId}");
        BeginStep();
        Resume(() => debugger.StepIn(threadId));
    }

    /// <summary>
    /// Step out (execute until returning from current method)
    /// </summary>
    public void StepOut(int threadId)
    {
        var debugger = RequireLiveDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepOut", $"Thread {threadId}");
        BeginStep();
        Resume(() => debugger.StepOut(threadId));
    }

    /// <summary>
    /// What the debuggee threw, read off the thread it is stopped on. The stop itself carries none
    /// of this - not even the type - so this is the only way to learn what the exception was.
    /// Bounded by the evaluation timeout rather than the operation one, because that is what it is:
    /// four property getters run in the target, the same cost as four EvaluateExpression calls.
    /// </summary>
    public async Task<ThrownException> GetExceptionInfo(int threadId)
    {
        var debugger = RequireLiveDebugger();

        // Off the caller's thread, as evaluation is everywhere else here: the request blocks until
        // the adapter has run all four getters
        return await Task.Run(() => debugger.GetException(threadId)).WaitAsync(_evaluationTimeout);
    }

    /// <summary>
    /// Evaluate a C# expression in the context of a stack frame
    /// </summary>
    public async Task<EvaluationResult> EvaluateExpression(string expression, int frameId)
    {
        var debugger = RequireLiveDebugger();

        return await Task.Run(() => debugger.Evaluate(expression, frameId)).WaitAsync(_evaluationTimeout);
    }

    /// <summary>
    /// Detach from process. Returns false when the teardown did not land, leaving the debuggee's
    /// fate unknown: a launched program may still be running, an attached one may still be
    /// suspended. Callers that report the outcome must not claim either was released.
    /// </summary>
    public bool Detach()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));
        }

        return DetachCore();
    }

    /// <summary>
    /// Tears the debugger down without the disposed guard, so it is safe to call from Dispose
    /// </summary>
    private bool DetachCore()
    {
        // Serialized against an in-flight resume of an ignored exception, which runs on its own
        // thread and would otherwise be talking to a debugger that is being disconnected
        lock (_teardownLock)
            return DetachCoreUnsynchronized();
    }

    private bool DetachCoreUnsynchronized()
    {
        DapDebugger? debuggerToRelease;
        bool weStartedIt;

        lock (_stateLock)
        {
            if (_debugger == null)
            {
                _attachedProcessId = null;
                _launchedProgram = null;
                _phase = SessionPhase.Idle;

                // Nothing to tear down, so the last real teardown's outcome still stands. This is
                // what makes Dispose after a failed Detach keep reporting the program as possibly
                // alive rather than overwriting that with a vacuous success.
                return _releasedCleanly;
            }

            debuggerToRelease = _debugger;
            weStartedIt = _launchedProgram != null;

            // Unsubscribe from events
            debuggerToRelease.OnStopped -= OnDebuggerStopped;
            debuggerToRelease.OnContinued -= OnDebuggerContinued;
            debuggerToRelease.OnExited -= OnDebuggerExited;
            debuggerToRelease.OnOutput -= OnDebuggerOutput;
            debuggerToRelease.OnBreakpointChanged -= OnDebuggerBreakpointChanged;

            _debugger = null;
            _attachedProcessId = null;
            _launchedProgram = null;
            _phase = SessionPhase.Idle;
            _isRunning = false;
            _breakpoints.Clear();
            _lastBreakpoint = null;
            ClearStopState();
        }

        // Whether the disconnect that carries the terminate returned at all. Nothing here can confirm
        // the kill: SharpDbg swallows a terminate failure and reports success, and it never tells us
        // the pid, so there is no process to look for afterwards. A disconnect that threw or timed
        // out is the only warning available that the program may have survived.
        var releasedCleanly = true;

        // Release outside the lock to avoid potential deadlocks. Without Disconnect the debuggee is
        // never let go by ICorDebug and stays suspended for the rest of its life.
        //
        // The step below is bounded and its failure contained, because Dispose is what has to run:
        // SharpDbg serializes requests behind one lock, so a handler that hangs would otherwise take
        // the disconnect with it, and disposing the adapter is the only release that does not go
        // through that lock. An unbounded step here would also strand _teardownLock and with it every
        // later operation on this session.
        try
        {
            // A program we started is ours to clean up. Terminating a running debuggee needs it
            // synchronized, and SharpDbg does that itself since 0.1.13 - Terminate stops the process
            // before terminating it. Up to 0.1.12 it did not, and the failure was swallowed, so this
            // paused first to make the terminate land. Measured on the way in: with the pause gone,
            // 0.1.12 leaks the debuggee on 5 of 5 runs and 0.1.13 kills it on 5 of 5.
            debuggerToRelease.Disconnect(terminateDebuggee: weStartedIt, _operationTimeout);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "Detach", ex);

            // Whichever kind of debuggee this was, the release did not land: a program we started
            // was never asked to die, and a process we merely attached to was never let go by
            // ICorDebug and stays suspended for the rest of its life.
            releasedCleanly = false;
        }
        finally
        {
            try
            {
                debuggerToRelease.Dispose();
            }
            catch (Exception ex)
            {
                McpLogger.LogDebugSessionError(_sessionId, "ReleaseAdapter", ex);
            }
        }

        lock (_stateLock)
            _releasedCleanly = releasedCleanly;

        return releasedCleanly;
    }

    /// <summary>
    /// Resolve the debugger for an operation that needs a process to exist. A launched program that
    /// has not been started yet has none: it is described to the debugger and nothing more.
    /// </summary>
    private DapDebugger RequireLiveDebugger()
    {
        lock (_stateLock)
        {
            var debugger = RequireDebugger();

            if (_phase != SessionPhase.Live)
                throw new InvalidOperationException(
                    $"{_launchedProgram} has been launched but not started. Call start_program to run it.");

            return debugger;
        }
    }

    /// <summary>
    /// Resolve the debugger for an operation that a launched program accepts before it runs, which
    /// is what breakpoints are for: they have to be in place before the first line executes.
    /// </summary>
    private DapDebugger RequireDebugger()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (_phase == SessionPhase.Idle)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            return _debugger;
        }
    }

    /// <summary>
    /// Marks the process as running before it is actually resumed, so that a stop event arriving
    /// during the resume call is not overwritten by this thread afterwards.
    /// </summary>
    private bool TryBeginResume()
    {
        lock (_stateLock)
        {
            if (_isRunning)
                return false;

            ClearStopState();
            _isRunning = true;
            return true;
        }
    }

    private void Resume(Action resume)
    {
        try
        {
            resume();
        }
        catch
        {
            lock (_stateLock)
            {
                _isRunning = false;
            }

            throw;
        }
    }

    /// <summary>
    /// Stepping requires a stopped process with an active frame; stepping a running one
    /// surfaces an opaque COM failure instead of a usable error.
    /// </summary>
    private void BeginStep()
    {
        if (!TryBeginResume())
            throw new InvalidOperationException(
                "Process is running. It must be stopped at a breakpoint before stepping.");
    }

    /// <summary>
    /// DAP's continue, pause and step all take a thread. The one the debuggee last stopped on is the
    /// right answer whenever there is one; otherwise any thread will do, since SharpDbg stops and
    /// resumes the whole process regardless of which thread is named.
    /// </summary>
    private int StoppedThreadOrFirst(DapDebugger debugger)
    {
        lock (_stateLock)
        {
            if (_lastStoppedThreadId is { } stopped)
                return stopped;
        }

        return debugger.GetThreads().FirstOrDefault().Id;
    }

    /// <summary>
    /// As above, but null rather than a hang when the adapter does not answer in time. Only the
    /// retained stopped thread is free; anything else costs a request to the adapter.
    /// </summary>
    private int? StoppedThreadOrFirst(DapDebugger debugger, TimeSpan timeout)
    {
        lock (_stateLock)
        {
            if (_lastStoppedThreadId is { } stopped)
                return stopped;
        }

        return debugger.TryGetThreads(timeout)?.FirstOrDefault().Id;
    }

    private void ClearStopState()
    {
        _currentLocation = null;
        _lastStopReason = null;
        _lastStoppedThreadId = null;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (condition())
                return true;

            if (DateTime.UtcNow >= deadline)
                return false;

            Thread.Sleep(PollInterval);
        }
    }

    private void LogMessage(string message)
    {
        // Log to stderr for MCP server
        Console.Error.WriteLine($"[Session {_sessionId}] {message}");
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        DetachCore();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents the execution state of a debug session
/// </summary>
public record ExecutionState(
    bool IsRunning,
    bool IsAttached,
    int? ProcessId,
    string? CurrentLocation = null,
    BreakpointHitInfo? LastBreakpoint = null,
    int? StoppedThreadId = null,
    string? StopReason = null,
    int ExceptionsSeen = 0,
    int ExceptionsIgnored = 0,
    string? LaunchedProgram = null,
    bool Started = true);

/// <summary>
/// A line the debuggee wrote, and which stream it came out of
/// </summary>
public record OutputLine(string Text, bool IsError);

/// <summary>
/// What the session does when the debuggee stops on a first-chance exception
/// </summary>
public enum ExceptionBreakMode
{
    /// <summary>
    /// Stay stopped on every exception, including ones the program catches itself. This is what
    /// ManagedDebugger does on its own and stays the default, so nothing is hidden by surprise.
    /// </summary>
    Always,

    /// <summary>
    /// Resume the debuggee whenever it stops on an exception, so a program that throws routinely
    /// can be debugged with breakpoints without being interrupted by its own caught exceptions.
    /// </summary>
    Never
}

/// <summary>
/// Information about a breakpoint that was hit
/// </summary>
public record BreakpointHitInfo(
    int BreakpointId,
    string? FilePath,
    int Line,
    int ThreadId);

/// <summary>
/// A breakpoint the session intends to have set, and what the debugger made of it
/// </summary>
internal sealed class TrackedBreakpoint(int id, string filePath, int line)
{
    public int Id { get; } = id;

    public string FilePath { get; } = filePath;

    /// <summary>The adapter's own id for this breakpoint, refreshed on every re-send</summary>
    public int AdapterId { get; set; }

    /// <summary>Line the caller asked for</summary>
    public int Line { get; } = line;

    /// <summary>Line the PDB actually bound to, when it differs from the requested one</summary>
    public int? ResolvedLine { get; set; }

    public string? Condition { get; set; }

    /// <summary>Hit-count condition, e.g. "5", "&gt;=3", "%2"</summary>
    public string? HitCondition { get; set; }

    public bool Verified { get; set; }

    public string? Message { get; set; }

    public BreakpointResult ToResult() => new(Id, FilePath, Line, Verified, Message, Condition, HitCondition);
}

internal sealed class TrackedFunctionBreakpoint(int id, string functionName)
{
    public int Id { get; } = id;

    /// <summary>Name pattern the caller asked for, e.g. "Program.Work" or "Work(int)"</summary>
    public string FunctionName { get; } = functionName;

    /// <summary>The adapter's own id for this breakpoint, refreshed on every re-send</summary>
    public int AdapterId { get; set; }

    /// <summary>Where the name actually bound, one entry per matching method</summary>
    public IReadOnlyList<BoundLocation> BoundLocations { get; set; } = [];

    public string? Condition { get; set; }

    /// <summary>Hit-count condition, e.g. "5", "&gt;=3", "%2"</summary>
    public string? HitCondition { get; set; }

    public bool Verified { get; set; }

    public string? Message { get; set; }

    public FunctionBreakpointResult ToResult() =>
        new(Id, FunctionName, Verified, Message, BoundLocations, Condition, HitCondition);
}

/// <summary>
/// Result of setting a breakpoint
/// </summary>
public record BreakpointResult(
    int Id,
    string FilePath,
    int Line,
    bool Verified,
    string? Message,
    string? Condition = null,
    string? HitCondition = null);

/// <summary>
/// Result of setting a function breakpoint. A single name can bind in more than one place, so the
/// locations it bound to are what tells the caller which methods it actually matched.
/// </summary>
public record FunctionBreakpointResult(
    int Id,
    string FunctionName,
    bool Verified,
    string? Message,
    IReadOnlyList<BoundLocation> BoundLocations,
    string? Condition = null,
    string? HitCondition = null);

/// <summary>
/// A source location a function breakpoint bound to
/// </summary>
public record BoundLocation(string FilePath, int Line);

/// <summary>
/// Information about a thread
/// </summary>
public record ThreadInfo(
    int Id,
    string Name);

/// <summary>
/// What a thread is stopped on, as far as the debugger can be made to say. Every field but the type
/// comes from running a property getter in the target.
/// </summary>
public record ThrownException(
    string TypeName,
    string? Message,
    int? HResult,
    string? Source,
    string? StackTrace);

/// <summary>
/// Result of evaluating an expression
/// </summary>
public record EvaluationResult(
    string Result,
    string? Type,
    int VariablesReference);

/// <summary>
/// One frame of a stack trace. Ours rather than the debugger's, so the type the tools and tests see
/// does not change shape when an unsupported internal does.
/// </summary>
public record StackFrameInfo(
    int Id,
    string Name,
    int Line,
    int EndLine,
    int Column,
    int EndColumn,
    string? Source);

/// <summary>
/// A variable, or a member of one. A non-zero VariablesReference can be expanded, and only until
/// the process resumes.
/// </summary>
public record VariableInfo(
    string Name,
    string Value,
    string? Type,
    int VariablesReference);
