using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.ResponseModels;
using SharpDbg.MCP.Logging;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Represents a debugging session that wraps ManagedDebugger
/// </summary>
public class DebugSession : IDisposable
{
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BreakpointVerificationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly int _sessionId;
    private readonly object _stateLock = new();
    private readonly Dictionary<(string FilePath, int Line), BreakpointResult> _breakpoints = new();
    private ManagedDebugger? _debugger;
    private int? _attachedProcessId;
    private bool _disposed;
    private string? _currentLocation;
    private string? _lastStopReason;
    private int? _lastStoppedThreadId;
    private BreakpointHitInfo? _lastBreakpoint;

    public int SessionId => _sessionId;

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

    public DebugSession(int sessionId)
    {
        _sessionId = sessionId;
    }

    /// <summary>
    /// Attach to a process
    /// </summary>
    public void Attach(int processId)
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

            // Subscribe to debugger events. OnStopped only reports pause/exception/entry stops;
            // breakpoint hits and completed steps arrive on OnStopped2 (which also carries the
            // source location), so both must be handled to observe every stop.
            debugger.OnStopped += OnDebuggerStopped;
            debugger.OnStopped2 += OnDebuggerStoppedAtLocation;
            debugger.OnContinued += OnDebuggerContinued;
            debugger.OnExited += OnDebuggerExited;
            debugger.OnOutput += OnDebuggerOutput;
            debugger.OnBreakpointChanged += OnDebuggerBreakpointChanged;

            _debugger = debugger;
            _attachedProcessId = processId;
            _lastBreakpoint = null;
            ClearStopState();
        }

        try
        {
            debugger.Attach(processId);
            debugger.ConfigurationDone();

            // ConfigurationDone kicks the attach off on a background thread and returns before the
            // process is available. Without waiting, a Continue or SetBreakpoint issued right after
            // attaching is silently applied to a null process.
            if (!WaitFor(() => debugger.IsRunning, AttachTimeout))
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
        lock (_stateLock)
        {
            _lastStoppedThreadId = threadId;
            _lastStopReason = reason;
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Stopped", $"Thread {threadId}: {reason}");
        LogMessage($"Stopped on thread {threadId}: {reason}");
    }

    /// <summary>
    /// Handles stops that carry a source location (breakpoint hits and completed steps)
    /// </summary>
    private void OnDebuggerStoppedAtLocation(int threadId, string filePath, int line, string reason)
    {
        lock (_stateLock)
        {
            _lastStoppedThreadId = threadId;
            _lastStopReason = reason;
            _currentLocation = $"{filePath}:{line}";

            if (reason == "breakpoint")
            {
                var breakpointId = _breakpoints.TryGetValue((filePath, line), out var known) ? known.Id : 0;
                _lastBreakpoint = new BreakpointHitInfo(breakpointId, filePath, line, threadId);
            }
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Stopped", $"Thread {threadId} at {filePath}:{line}: {reason}");
        LogMessage($"Stopped on thread {threadId} at {filePath}:{line}: {reason}");
    }

    private void OnDebuggerContinued(int threadId)
    {
        lock (_stateLock)
        {
            ClearStopState();
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Continued", $"Thread {threadId}");
        LogMessage($"Continued on thread {threadId}");
    }

    private void OnDebuggerExited()
    {
        lock (_stateLock)
        {
            ClearStopState();
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Exited", "Process terminated");
        LogMessage("Process exited");
    }

    private void OnDebuggerOutput(string message)
    {
        McpLogger.LogDebug("Session {SessionId} | Output: {Message}", _sessionId, message);
        LogMessage($"Output: {message}");
    }

    private void OnDebuggerBreakpointChanged(SharpDbg.Infrastructure.Debugger.BreakpointManager.BreakpointInfo breakpoint)
    {
        lock (_stateLock)
        {
            _breakpoints[(breakpoint.FilePath, breakpoint.Line)] = new BreakpointResult(
                breakpoint.Id,
                breakpoint.FilePath,
                breakpoint.Line,
                breakpoint.Verified,
                breakpoint.Message);
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "BreakpointChanged",
            $"{breakpoint.FilePath}:{breakpoint.Line} (verified={breakpoint.Verified})");
        LogMessage($"Breakpoint changed: {breakpoint.FilePath}:{breakpoint.Line} (verified={breakpoint.Verified})");
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

            // ManagedDebugger owns the authoritative running flag - it clears it on the ICorDebug
            // callback thread the moment the process stops, so mirroring it here would drift.
            var isRunning = _debugger?.IsRunning ?? false;

            return new ExecutionState(
                isRunning,
                _attachedProcessId.HasValue,
                _attachedProcessId,
                _currentLocation,
                _lastBreakpoint,
                _lastStoppedThreadId,
                _lastStopReason);
        }
    }

    /// <summary>
    /// Set a breakpoint at a specific file and line
    /// </summary>
    public BreakpointResult SetBreakpoint(string filePath, int line)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "SetBreakpoint", $"{filePath}:{line}");

        try
        {
            var breakpoints = debugger.SetBreakpoints(filePath, new[] { line });

            if (breakpoints.Count == 0)
                throw new InvalidOperationException("Failed to create breakpoint");

            var bp = breakpoints[0];
            var result = new BreakpointResult(bp.Id, bp.FilePath, bp.Line, bp.Verified, bp.Message);

            lock (_stateLock)
            {
                _breakpoints[(result.FilePath, result.Line)] = result;
            }

            // A breakpoint created before the target module's symbols have loaded comes back
            // unverified and binds later, on the module-load callback. Wait for that instead of
            // reporting a pending breakpoint that is about to become active.
            if (!result.Verified)
                result = WaitForVerification(result);

            McpLogger.LogDebugSessionEvent(_sessionId, "BreakpointSet",
                $"ID {result.Id} at {result.FilePath}:{result.Line} (verified={result.Verified})");

            return result;
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "SetBreakpoint", ex);
            throw;
        }
    }

    private BreakpointResult WaitForVerification(BreakpointResult breakpoint)
    {
        var key = (breakpoint.FilePath, breakpoint.Line);
        var current = breakpoint;

        WaitFor(() =>
        {
            lock (_stateLock)
            {
                if (_breakpoints.TryGetValue(key, out var tracked))
                    current = tracked;
            }

            return current.Verified;
        }, BreakpointVerificationTimeout);

        return current;
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
    /// Get variables for a stack frame by frame ID
    /// </summary>
    public async Task<List<VariableInfo>> GetVariables(int frameId)
    {
        var debugger = RequireDebugger();

        // Call async methods outside the lock to avoid deadlocks
        var scopes = debugger.GetScopes(frameId);
        if (scopes.Count == 0)
            return new List<VariableInfo>();

        // Get variables from the first scope (typically "Locals")
        return await debugger.GetVariables(scopes[0].VariablesReference);
    }

    /// <summary>
    /// Continue execution until next breakpoint or exit.
    /// Returns false when the process was already running and nothing needed resuming.
    /// </summary>
    public bool Continue()
    {
        var debugger = RequireDebugger();

        // Continuing an already-running process throws CORDBG_E_SUPERFLOUS_CONTINUE out of COM.
        if (debugger.IsRunning)
        {
            McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Already running - ignored");
            return false;
        }

        McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Resuming execution");

        lock (_stateLock)
        {
            ClearStopState();
        }

        debugger.Continue();
        return true;
    }

    /// <summary>
    /// Pause execution (break into debugger)
    /// </summary>
    public void Pause()
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "Pause", "Breaking execution");
        debugger.Pause();
    }

    /// <summary>
    /// Step over (execute current line and stop at next line in same method)
    /// </summary>
    public void StepOver(int threadId)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepOver", $"Thread {threadId}");
        BeginStep(debugger);
        debugger.StepNext(threadId);
    }

    /// <summary>
    /// Step into (execute current line and stop at first line of called method)
    /// </summary>
    public void StepInto(int threadId)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepInto", $"Thread {threadId}");
        BeginStep(debugger);
        debugger.StepIn(threadId);
    }

    /// <summary>
    /// Step out (execute until returning from current method)
    /// </summary>
    public void StepOut(int threadId)
    {
        var debugger = RequireDebugger();

        McpLogger.LogDebugSessionEvent(_sessionId, "StepOut", $"Thread {threadId}");
        BeginStep(debugger);
        debugger.StepOut(threadId);
    }

    /// <summary>
    /// Evaluate a C# expression in the context of a stack frame
    /// </summary>
    public async Task<EvaluationResult> EvaluateExpression(string expression, int frameId)
    {
        var debugger = RequireDebugger();

        // Call async methods outside the lock to avoid deadlocks
        var (result, type, variablesReference) = await debugger.Evaluate(expression, frameId);
        return new EvaluationResult(result, type, variablesReference);
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
        ManagedDebugger? debuggerToDispose;

        lock (_stateLock)
        {
            if (_debugger == null)
            {
                _attachedProcessId = null;
                return;
            }

            debuggerToDispose = _debugger;

            // Unsubscribe from events
            debuggerToDispose.OnStopped -= OnDebuggerStopped;
            debuggerToDispose.OnStopped2 -= OnDebuggerStoppedAtLocation;
            debuggerToDispose.OnContinued -= OnDebuggerContinued;
            debuggerToDispose.OnExited -= OnDebuggerExited;
            debuggerToDispose.OnOutput -= OnDebuggerOutput;
            debuggerToDispose.OnBreakpointChanged -= OnDebuggerBreakpointChanged;

            _debugger = null;
            _attachedProcessId = null;
            _breakpoints.Clear();
            _lastBreakpoint = null;
            ClearStopState();
        }

        // Detach and dispose outside the lock to avoid potential deadlocks.
        try
        {
            // Dispose only tears down our own state - without Disconnect the debuggee is never
            // released by ICorDebug and stays suspended for the rest of its life.
            debuggerToDispose.Disconnect(terminateDebuggee: false);
        }
        catch (Exception ex)
        {
            McpLogger.LogDebugSessionError(_sessionId, "Detach", ex);
        }
        finally
        {
            debuggerToDispose.Dispose();
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
    /// Stepping requires a stopped process with an active frame; stepping a running one
    /// surfaces an opaque COM failure instead of a usable error.
    /// </summary>
    private void BeginStep(ManagedDebugger debugger)
    {
        if (debugger.IsRunning)
            throw new InvalidOperationException(
                "Process is running. It must be stopped at a breakpoint before stepping.");

        lock (_stateLock)
        {
            ClearStopState();
        }
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
    string? StopReason = null);

/// <summary>
/// Information about a breakpoint that was hit
/// </summary>
public record BreakpointHitInfo(
    int BreakpointId,
    string? FilePath,
    int Line,
    int ThreadId);

/// <summary>
/// Result of setting a breakpoint
/// </summary>
public record BreakpointResult(
    int Id,
    string FilePath,
    int Line,
    bool Verified,
    string? Message);

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
