using SharpDbg.MCP.Configuration;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Manages multiple debug sessions
/// </summary>
public class DebugSessionManager : IDisposable
{
    private readonly Dictionary<int, DebugSession> _sessions = new();
    private readonly ServerConfiguration _configuration;
    private int _nextSessionId = 1;
    private readonly object _lock = new();
    private bool _disposed;

    public DebugSessionManager(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <summary>
    /// Create a new debug session
    /// </summary>
    public DebugSession CreateSession()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        lock (_lock)
        {
            return CreateSessionCore();
        }
    }

    /// <summary>
    /// Caller already holds the lock. Kept separate so Resolve and AcquireForDebuggee can create a
    /// session without releasing and retaking it, which would let two callers past the limit.
    /// </summary>
    private DebugSession CreateSessionCore()
    {
        if (_sessions.Count >= _configuration.MaxConcurrentSessions)
            throw new InvalidOperationException(
                $"Maximum number of concurrent debug sessions ({_configuration.MaxConcurrentSessions}) reached. " +
                "Close one with close_session, or raise SHARPDBG_MAX_SESSIONS.");

        var sessionId = _nextSessionId++;
        var session = new DebugSession(sessionId, _configuration);
        _sessions[sessionId] = session;
        return session;
    }

    /// <summary>
    /// Get an existing session by ID
    /// </summary>
    public DebugSession? GetSession(int sessionId)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }
    }

    /// <summary>
    /// Get all active sessions
    /// </summary>
    public List<DebugSession> GetAllSessions()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        lock (_lock)
        {
            return _sessions.Values.ToList();
        }
    }

    /// <summary>
    /// Closes and removes a session. Returns false when a program it launched could not be suspended
    /// before the terminate, which is the one case where it may have outlived the session.
    /// </summary>
    public bool CloseSession(int sessionId)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        DebugSession? sessionToDispose = null;

        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                sessionToDispose = session;
                _sessions.Remove(sessionId);
            }
        }

        // Dispose outside the lock to avoid potential deadlocks
        sessionToDispose?.Dispose();

        return sessionToDispose?.SuspendedForTerminate ?? true;
    }

    /// <summary>
    /// The session a tool call is about. Omitting the id is the normal case and keeps working as
    /// long as there is nothing to be ambiguous about: it creates the first session, and picks the
    /// only one after that. With several sessions open the id becomes required, because guessing
    /// which process a breakpoint or a continue was meant for is worse than asking.
    /// </summary>
    public DebugSession Resolve(int? sessionId)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        lock (_lock)
        {
            if (sessionId.HasValue)
            {
                return _sessions.TryGetValue(sessionId.Value, out var requested)
                    ? requested
                    : throw new ArgumentException(
                        $"There is no debug session with id {sessionId.Value}. Use list_sessions to see the open ones.",
                        nameof(sessionId));
            }

            if (_sessions.Count == 0)
                return CreateSessionCore();

            if (_sessions.Count > 1)
                throw new InvalidOperationException(
                    $"{_sessions.Count} debug sessions are open, so session_id is required. " +
                    "Use list_sessions to see which is which.");

            return _sessions.Values.First();
        }
    }

    /// <summary>
    /// A session to put a debuggee in, whether attached to or launched: an open one that has none,
    /// or a new one. This is what makes a second attach open a second session rather than fail.
    /// </summary>
    public DebugSession AcquireForDebuggee(int? sessionId)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        if (sessionId.HasValue)
            return Resolve(sessionId);

        lock (_lock)
        {
            var free = _sessions.Values.FirstOrDefault(s => !s.IsAttached);

            return free ?? CreateSessionCore();
        }
    }

    public void Dispose()
    {
        List<DebugSession>? sessionsToDispose = null;

        lock (_lock)
        {
            if (_disposed)
                return;

            sessionsToDispose = _sessions.Values.ToList();
            _sessions.Clear();
            _disposed = true;
        }

        // Dispose all sessions outside the lock to avoid potential deadlocks
        if (sessionsToDispose != null)
        {
            foreach (var session in sessionsToDispose)
            {
                session.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }
}
