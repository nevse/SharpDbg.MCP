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
            if (_sessions.Count >= _configuration.MaxConcurrentSessions)
                throw new InvalidOperationException(
                    $"Maximum number of concurrent debug sessions ({_configuration.MaxConcurrentSessions}) reached. " +
                    "Close an existing session or raise SHARPDBG_MAX_SESSIONS.");

            var sessionId = _nextSessionId++;
            var session = new DebugSession(sessionId, _configuration);
            _sessions[sessionId] = session;
            return session;
        }
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
    /// Close and remove a session
    /// </summary>
    public void CloseSession(int sessionId)
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
    }

    /// <summary>
    /// Get or create the current session (for single-session mode)
    /// </summary>
    public DebugSession GetOrCreateCurrentSession()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugSessionManager));

        lock (_lock)
        {
            // For MVP, we'll just use a single session
            if (_sessions.Count == 0)
            {
                return CreateSession();
            }

            return _sessions.Values.First();
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
