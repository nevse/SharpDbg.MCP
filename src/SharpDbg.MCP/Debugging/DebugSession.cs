using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Logging;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Represents a debugging session that wraps ManagedDebugger
/// </summary>
public class DebugSession : IDisposable
{
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
    private int _nextBreakpointId = 1;
    private ManagedDebugger? _debugger;
    private int? _attachedProcessId;
    private bool _disposed;
    private bool _isRunning;
    private string? _currentLocation;
    private string? _lastStopReason;
    private int? _lastStoppedThreadId;
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
                    && _attachedProcessId.HasValue;

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

    public bool IsAttached
    {
        get
        {
            lock (_stateLock)
            {
                return _attachedProcessId.HasValue;
            }
        }
    }

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
        ManagedDebugger debugger;

        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (_attachedProcessId.HasValue)
                throw new InvalidOperationException("Already attached to a process");

            McpLogger.LogDebugSessionEvent(_sessionId, "Attaching", $"Process ID: {processId}");

            debugger = new ManagedDebugger(LogMessage);

            // Subscribe to debugger events. OnStopped only reports pause/exception stops; breakpoint
            // hits and completed steps arrive on OnStopped2 (which also carries the source
            // location), so both must be handled to observe every stop.
            debugger.OnStopped += OnDebuggerStopped;
            debugger.OnStopped2 += OnDebuggerStoppedAtLocation;
            debugger.OnContinued += OnDebuggerContinued;
            debugger.OnExited += OnDebuggerExited;
            debugger.OnOutput += OnDebuggerOutput;
            debugger.OnBreakpointChanged += OnDebuggerBreakpointChanged;

            _debugger = debugger;
            _attachedProcessId = processId;
            _lastBreakpoint = null;
            _isRunning = true;
            _exceptionsSeen = 0;
            _exceptionsIgnored = 0;
            ClearStopState();
        }

        try
        {
            debugger.Attach(processId, _justMyCode);
            await debugger.ConfigurationDone();

            // ConfigurationDone hands the attach to a fire-and-forget Task.Run, so the process is
            // still unusable when it returns. GetThreads stays empty until the process exists,
            // which makes it the cheapest signal that the attach has actually landed.
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

    private void OnDebuggerStopped(int threadId, string reason)
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
                && _attachedProcessId.HasValue;

            if (ignoring)
            {
                _exceptionsIgnored++;
            }
            else
            {
                _isRunning = false;
                _lastStoppedThreadId = threadId;
                _lastStopReason = reason;
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
                    ManagedDebugger? debugger;

                    lock (_stateLock)
                        debugger = _attachedProcessId.HasValue ? _debugger : null;

                    debugger?.HandleContinueRequest();
                }
                catch (Exception ex)
                {
                    // The process really is suspended now, so say so rather than leaving the session
                    // claiming it runs - that is the one thing worse than an unwanted stop
                    lock (_stateLock)
                    {
                        if (_attachedProcessId.HasValue)
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
    /// Handles stops that carry a source location (breakpoint hits and completed steps)
    /// </summary>
    private void OnDebuggerStoppedAtLocation(
        int threadId,
        string filePath,
        int line,
        int column,
        string reason,
        DecompiledSourceInfo? decompiledSource)
    {
        lock (_stateLock)
        {
            _isRunning = false;
            _lastStoppedThreadId = threadId;
            _lastStopReason = reason;
            _currentLocation = $"{filePath}:{line}";

            if (reason == "breakpoint")
            {
                // The stop reports the line the PDB resolved to, which is not necessarily the
                // line that was requested, and for a function breakpoint was never requested.
                _lastBreakpoint = new BreakpointHitInfo(
                    FindIdByLocation(filePath, line) ?? 0, filePath, line, threadId);
            }
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Stopped", $"Thread {threadId} at {filePath}:{line}: {reason}");
        LogMessage($"Stopped on thread {threadId} at {filePath}:{line}: {reason}");
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
    }

    private void OnDebuggerBreakpointChanged(BreakpointManager.BreakpointInfo breakpoint)
    {
        // A function breakpoint has no requested location to match on, so it is found by name
        var describedAs = breakpoint.FunctionName ?? $"{breakpoint.FilePath}:{breakpoint.Line}";

        lock (_stateLock)
        {
            if (breakpoint.FunctionName != null)
            {
                var trackedFunction = FindByFunctionName(breakpoint.FunctionName);
                if (trackedFunction != null)
                    ApplyDebuggerState(trackedFunction, breakpoint);
            }
            else
            {
                var tracked = FindByLocation(breakpoint.FilePath, breakpoint.Line);
                if (tracked != null)
                    ApplyDebuggerState(tracked, breakpoint);
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

            return new ExecutionState(
                _attachedProcessId.HasValue && _isRunning,
                _attachedProcessId.HasValue,
                _attachedProcessId,
                _currentLocation,
                _lastBreakpoint,
                _lastStoppedThreadId,
                _lastStopReason,
                _exceptionsSeen,
                _exceptionsIgnored);
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

    private bool RemoveFunctionBreakpoint(ManagedDebugger debugger, int breakpointId)
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
    private void ReapplyFile(ManagedDebugger debugger, string filePath)
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
            .Select(b => new SharpDbgBreakpointRequest(b.Line, b.Condition, b.HitCondition))
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
    private void ReapplyFunctionBreakpoints(ManagedDebugger debugger)
    {
        List<TrackedFunctionBreakpoint> all;

        lock (_stateLock)
        {
            all = _functionBreakpoints.Values.OrderBy(b => b.Id).ToList();
        }

        var requests = all
            .Select(b => new SharpDbgFunctionBreakpointRequest(b.FunctionName, b.Condition, b.HitCondition))
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
    /// ManagedDebugger keeps its modules in a plain Dictionary that the module-load callback writes
    /// to on the debugger's own thread, while binding enumerates it on ours, so a call made while
    /// the debuggee is still loading assemblies can fail with "Collection was modified". Retrying is
    /// safe because both SetBreakpoints and SetFunctionBreakpoints replace a whole set rather than
    /// adding to one, so a call that half-finished leaves nothing behind to duplicate.
    /// The exception type is the only signal available - the message is the framework's and is
    /// localized - so the retries are kept few and short, and anything that survives them is
    /// reported to the caller.
    /// </summary>
    private static List<BreakpointManager.BreakpointInfo> RetryWhileModulesLoad(
        Func<List<BreakpointManager.BreakpointInfo>> apply)
    {
        const int MaxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return apply();
            }
            catch (InvalidOperationException) when (attempt < MaxAttempts)
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
        TResult? current = null;

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

    private static void ApplyDebuggerState(TrackedBreakpoint tracked, BreakpointManager.BreakpointInfo info)
    {
        tracked.Verified = info.Verified;
        tracked.Message = info.Message;
        tracked.ResolvedLine = info.ResolvedBreakpointFromPdb?.StartLine;
    }

    private static void ApplyDebuggerState(
        TrackedFunctionBreakpoint tracked,
        BreakpointManager.BreakpointInfo info)
    {
        tracked.Verified = info.Verified;
        tracked.Message = info.Message;

        // One name can match several methods - overloads, or the same name in several modules -
        // and each binding knows the source location it resolved to.
        tracked.BoundLocations = info.FunctionBindings
            .Select(b => new BoundLocation(b.Source.DocumentPath, b.Source.StartLine))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Get stack trace for a specific thread
    /// </summary>
    public List<StackFrameInfo> GetStackTrace(int threadId)
    {
        return RequireDebugger().GetStackTrace(threadId);
    }

    /// <summary>
    /// Get all threads in the process
    /// </summary>
    public List<ThreadInfo> GetThreads()
    {
        var threads = RequireDebugger().GetThreads();
        return threads.Select(t => new ThreadInfo(t.id, t.name)).ToList();
    }

    /// <summary>
    /// Get variables for a stack frame by frame ID.
    /// ManagedDebugger exposes a single "Locals" scope per frame which already covers the current
    /// exception, the arguments and the locals.
    /// </summary>
    public async Task<List<VariableInfo>> GetVariables(int frameId)
    {
        var debugger = RequireDebugger();

        // Call async methods outside the lock to avoid deadlocks
        var scopes = debugger.GetScopes(frameId);
        if (scopes.Count == 0)
            return new List<VariableInfo>();

        return await debugger.GetVariables(scopes[0].VariablesReference);
    }

    /// <summary>
    /// Expand a variables reference into its members. References come from GetVariables or
    /// EvaluateExpression and are invalidated as soon as the process resumes.
    /// </summary>
    public async Task<List<VariableInfo>> ExpandVariable(int variablesReference)
    {
        var debugger = RequireDebugger();

        // Call async methods outside the lock to avoid deadlocks
        return await debugger.GetVariables(variablesReference);
    }

    /// <summary>
    /// Continue execution until next breakpoint or exit.
    /// Returns false when the process was already running and nothing needed resuming.
    /// </summary>
    public bool Continue()
    {
        var debugger = RequireDebugger();

        // Continuing an already-running process throws CORDBG_E_SUPERFLOUS_CONTINUE out of COM.
        if (!TryBeginResume())
        {
            McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Already running - ignored");
            return false;
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Resuming execution");
        Resume(debugger.HandleContinueRequest);
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
        RequireDebugger();

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
    /// Pause execution (break into debugger)
    /// </summary>
    public void Pause()
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "Pause", "Breaking execution");
        debugger.Pause();

        // Stop() is synchronous and raises no stop event of its own.
        lock (_stateLock)
        {
            _isRunning = false;
            _lastStopReason = "pause";
        }
    }

    /// <summary>
    /// Step over (execute current line and stop at next line in same method)
    /// </summary>
    public void StepOver(int threadId)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepOver", $"Thread {threadId}");
        BeginStep();
        Resume(() => debugger.StepNext(threadId));
    }

    /// <summary>
    /// Step into (execute current line and stop at first line of called method)
    /// </summary>
    public void StepInto(int threadId)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepInto", $"Thread {threadId}");
        BeginStep();
        Resume(() => debugger.StepIn(threadId));
    }

    /// <summary>
    /// Step out (execute until returning from current method)
    /// </summary>
    public void StepOut(int threadId)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepOut", $"Thread {threadId}");
        BeginStep();
        Resume(() => debugger.StepOut(threadId));
    }

    /// <summary>
    /// Evaluate a C# expression in the context of a stack frame
    /// </summary>
    public async Task<EvaluationResult> EvaluateExpression(string expression, int frameId)
    {
        var debugger = RequireDebugger();

        // Call async methods outside the lock to avoid deadlocks
        var evaluated = await debugger.Evaluate(expression, frameId).WaitAsync(_evaluationTimeout);
        return new EvaluationResult(evaluated.Value, evaluated.Type, evaluated.VariablesReference);
    }

    /// <summary>
    /// Detach from process
    /// </summary>
    public void Detach()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));
        }

        DetachCore();
    }

    /// <summary>
    /// Tears the debugger down without the disposed guard, so it is safe to call from Dispose
    /// </summary>
    private void DetachCore()
    {
        // Serialized against an in-flight resume of an ignored exception, which runs on its own
        // thread and would otherwise be talking to a debugger that is being disconnected
        lock (_teardownLock)
            DetachCoreUnsynchronized();
    }

    private void DetachCoreUnsynchronized()
    {
        ManagedDebugger? debuggerToRelease;

        lock (_stateLock)
        {
            if (_debugger == null)
            {
                _attachedProcessId = null;
                return;
            }

            debuggerToRelease = _debugger;

            // Unsubscribe from events
            debuggerToRelease.OnStopped -= OnDebuggerStopped;
            debuggerToRelease.OnStopped2 -= OnDebuggerStoppedAtLocation;
            debuggerToRelease.OnContinued -= OnDebuggerContinued;
            debuggerToRelease.OnExited -= OnDebuggerExited;
            debuggerToRelease.OnOutput -= OnDebuggerOutput;
            debuggerToRelease.OnBreakpointChanged -= OnDebuggerBreakpointChanged;

            _debugger = null;
            _attachedProcessId = null;
            _isRunning = false;
            _breakpoints.Clear();
            _lastBreakpoint = null;
            ClearStopState();
        }

        // Release outside the lock to avoid potential deadlocks. Without Disconnect the debuggee is
        // never let go by ICorDebug and stays suspended for the rest of its life.
        try
        {
            debuggerToRelease.Disconnect(terminateDebuggee: false);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "Detach", ex);
        }
    }

    /// <summary>
    /// Resolve the debugger for an operation that requires an attached process
    /// </summary>
    private ManagedDebugger RequireDebugger()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
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
    int ExceptionsIgnored = 0);

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
/// Result of evaluating an expression
/// </summary>
public record EvaluationResult(
    string Result,
    string? Type,
    int VariablesReference);
