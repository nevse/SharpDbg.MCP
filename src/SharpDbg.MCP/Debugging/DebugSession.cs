using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.ResponseModels;
using SharpDbg.MCP.Logging;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Represents a debugging session that wraps ManagedDebugger
/// </summary>
public class DebugSession : IDisposable
{
    private readonly int _sessionId;
    private readonly object _stateLock = new();
    private ManagedDebugger? _debugger;
    private int? _attachedProcessId;
    private bool _disposed;
    private bool _isRunning;

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
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (_attachedProcessId.HasValue)
                throw new InvalidOperationException("Already attached to a process");

            McpLogger.LogDebugSessionEvent(_sessionId, "Attaching", $"Process ID: {processId}");

            try
            {
                _debugger = new ManagedDebugger(LogMessage);

                // Subscribe to debugger events
                _debugger.OnStopped += OnDebuggerStopped;
                _debugger.OnContinued += OnDebuggerContinued;
                _debugger.OnExited += OnDebuggerExited;
                _debugger.OnOutput += OnDebuggerOutput;
                _debugger.OnBreakpointChanged += OnDebuggerBreakpointChanged;

                _debugger.Attach(processId);
                _debugger.ConfigurationDone();
                _attachedProcessId = processId;
                _isRunning = false;

                McpLogger.LogDebugSessionEvent(_sessionId, "Attached", $"Successfully attached to process {processId}");
            }
            catch (Exception ex)
            {
                McpLogger.LogDebugSessionError(_sessionId, "Attach", ex);
                throw;
            }
        }
    }

    private void OnDebuggerStopped(int threadId, string reason)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }
        McpLogger.LogDebugSessionEvent(_sessionId, "Stopped", $"Thread {threadId}: {reason}");
        LogMessage($"Stopped on thread {threadId}: {reason}");
    }

    private void OnDebuggerContinued(int threadId)
    {
        lock (_stateLock)
        {
            _isRunning = true;
        }
        McpLogger.LogDebugSessionEvent(_sessionId, "Continued", $"Thread {threadId}");
        LogMessage($"Continued on thread {threadId}");
    }

    private void OnDebuggerExited()
    {
        lock (_stateLock)
        {
            _isRunning = false;
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

            return new ExecutionState(
                _isRunning,
                _attachedProcessId.HasValue,
                _attachedProcessId);
        }
    }

    /// <summary>
    /// Set a breakpoint at a specific file and line
    /// </summary>
    public BreakpointResult SetBreakpoint(string filePath, int line)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            McpLogger.LogDebugSessionEvent(_sessionId, "SetBreakpoint", $"{filePath}:{line}");

            try
            {
                var breakpoints = _debugger.SetBreakpoints(filePath, new[] { line });

                if (breakpoints.Count == 0)
                    throw new InvalidOperationException("Failed to create breakpoint");

                var bp = breakpoints[0];
                McpLogger.LogDebugSessionEvent(_sessionId, "BreakpointSet",
                    $"ID {bp.Id} at {bp.FilePath}:{bp.Line} (verified={bp.Verified})");

                return new BreakpointResult(
                    bp.Id,
                    bp.FilePath,
                    bp.Line,
                    bp.Verified,
                    bp.Message);
            }
            catch (Exception ex)
            {
                McpLogger.LogDebugSessionError(_sessionId, "SetBreakpoint", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Get stack trace for a specific thread
    /// </summary>
    public List<StackFrameInfo> GetStackTrace(int threadId)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            return _debugger.GetStackTrace(threadId);
        }
    }

    /// <summary>
    /// Get all threads in the process
    /// </summary>
    public List<ThreadInfo> GetThreads()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            var threads = _debugger.GetThreads();
            return threads.Select(t => new ThreadInfo(t.id, t.name)).ToList();
        }
    }

    /// <summary>
    /// Get variables for a stack frame by frame ID
    /// </summary>
    public async Task<List<VariableInfo>> GetVariables(int frameId)
    {
        ManagedDebugger? debugger;
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            debugger = _debugger;
        }

        // Call async methods outside the lock to avoid deadlocks
        var scopes = debugger.GetScopes(frameId);
        if (scopes.Count == 0)
            return new List<VariableInfo>();

        // Get variables from the first scope (typically "Locals")
        return await debugger.GetVariables(scopes[0].VariablesReference);
    }

    /// <summary>
    /// Continue execution until next breakpoint or exit
    /// </summary>
    public void Continue()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            McpLogger.LogDebugSessionEvent(_sessionId, "Continue", "Resuming execution");
            _debugger.Continue();
            _isRunning = true;
        }
    }

    /// <summary>
    /// Pause execution (break into debugger)
    /// </summary>
    public void Pause()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            McpLogger.LogDebugSessionEvent(_sessionId, "Pause", "Breaking execution");
            _debugger.Pause();
            _isRunning = false;
        }
    }

    /// <summary>
    /// Step over (execute current line and stop at next line in same method)
    /// </summary>
    public void StepOver(int threadId)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            McpLogger.LogDebugSessionEvent(_sessionId, "StepOver", $"Thread {threadId}");
            _debugger.StepNext(threadId);
            _isRunning = true;
        }
    }

    /// <summary>
    /// Step into (execute current line and stop at first line of called method)
    /// </summary>
    public void StepInto(int threadId)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            McpLogger.LogDebugSessionEvent(_sessionId, "StepInto", $"Thread {threadId}");
            _debugger.StepIn(threadId);
            _isRunning = true;
        }
    }

    /// <summary>
    /// Step out (execute until returning from current method)
    /// </summary>
    public void StepOut(int threadId)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            McpLogger.LogDebugSessionEvent(_sessionId, "StepOut", $"Thread {threadId}");
            _debugger.StepOut(threadId);
            _isRunning = true;
        }
    }

    /// <summary>
    /// Evaluate a C# expression in the context of a stack frame
    /// </summary>
    public async Task<EvaluationResult> EvaluateExpression(string expression, int frameId)
    {
        ManagedDebugger? debugger;
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                throw new InvalidOperationException("Not attached to a process");

            if (_debugger == null)
                throw new InvalidOperationException("Debugger not initialized");

            debugger = _debugger;
        }

        // Call async methods outside the lock to avoid deadlocks
        var (result, type, variablesReference) = await debugger.Evaluate(expression, frameId);
        return new EvaluationResult(result, type, variablesReference);
    }

    /// <summary>
    /// Detach from process
    /// </summary>
    public void Detach()
    {
        ManagedDebugger? debuggerToDispose = null;

        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DebugSession));

            if (!_attachedProcessId.HasValue)
                return;

            debuggerToDispose = _debugger;

            // Unsubscribe from events
            if (debuggerToDispose != null)
            {
                debuggerToDispose.OnStopped -= OnDebuggerStopped;
                debuggerToDispose.OnContinued -= OnDebuggerContinued;
                debuggerToDispose.OnExited -= OnDebuggerExited;
                debuggerToDispose.OnOutput -= OnDebuggerOutput;
                debuggerToDispose.OnBreakpointChanged -= OnDebuggerBreakpointChanged;
            }

            _debugger = null;
            _attachedProcessId = null;
            _isRunning = false;
        }

        // Dispose outside the lock to avoid potential deadlocks
        debuggerToDispose?.Dispose();
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

        Detach();
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
    BreakpointHitInfo? LastBreakpoint = null);

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
