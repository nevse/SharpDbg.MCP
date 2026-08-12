using Microsoft.Extensions.Logging;

namespace SharpDbg.MCP.Configuration;

/// <summary>
/// Configuration settings for the SharpDbg MCP Server
/// </summary>
public class ServerConfiguration
{
    /// <summary>
    /// Log level for the server (default: Information)
    /// Environment variable: SHARPDBG_LOG_LEVEL
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Maximum number of debug sessions open at once (default: 1). Raising it lets one server debug
    /// several processes at the same time, each with its own breakpoints and its own stops, selected
    /// by session_id. The default of one keeps that off unless it is asked for: every attach carries
    /// the risk of a native crash inside the debugging shim, and more sessions means more attaches.
    /// Environment variable: SHARPDBG_MAX_SESSIONS
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 1;

    /// <summary>
    /// Timeout in seconds for debugger operations (default: 30)
    /// Environment variable: SHARPDBG_OPERATION_TIMEOUT_SECONDS
    /// </summary>
    public int OperationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to allow attaching to processes owned by other users (default: false)
    /// Environment variable: SHARPDBG_ALLOW_OTHER_USER_PROCESSES
    /// </summary>
    public bool AllowOtherUserProcesses { get; set; } = false;

    /// <summary>
    /// Expression evaluation timeout in milliseconds (default: 5000)
    /// Environment variable: SHARPDBG_EVAL_TIMEOUT_MS
    /// </summary>
    public int ExpressionEvaluationTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// How long to wait for a breakpoint to bind before reporting it as unverified (default: 2000).
    /// A breakpoint set right after attaching can be answered unverified because the target
    /// module's symbols have not been processed yet; it binds on the module-load callback, which
    /// measures around 30ms. Waiting for that is what keeps callers from seeing a breakpoint that
    /// is about to become active, but the wait has to stay short, because a breakpoint that can
    /// never bind costs exactly this long.
    /// Environment variable: SHARPDBG_BREAKPOINT_BIND_TIMEOUT_MS
    /// </summary>
    public int BreakpointBindTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Enable detailed diagnostic logging for troubleshooting (default: false)
    /// Environment variable: SHARPDBG_ENABLE_DIAGNOSTICS
    /// </summary>
    public bool EnableDiagnostics { get; set; } = false;

    /// <summary>
    /// Restrict debugging to user code, skipping framework and third-party assemblies (default: true).
    /// Turning it off lets a step reach code that has no symbols, which the debugger then decompiles
    /// to find a location. That works, but the first module costs seconds - over 20 for
    /// System.Private.CoreLib - with the debuggee suspended throughout, before the result is cached.
    /// Environment variable: SHARPDBG_JUST_MY_CODE
    /// </summary>
    public bool JustMyCode { get; set; } = true;

    /// <summary>
    /// Server version
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Load configuration from environment variables
    /// </summary>
    public static ServerConfiguration LoadFromEnvironment()
    {
        var config = new ServerConfiguration();

        // Log level
        var logLevel = Environment.GetEnvironmentVariable("SHARPDBG_LOG_LEVEL");
        if (Enum.TryParse<LogLevel>(logLevel, true, out var parsedLevel))
        {
            config.LogLevel = parsedLevel;
        }

        // Max sessions
        var maxSessions = Environment.GetEnvironmentVariable("SHARPDBG_MAX_SESSIONS");
        if (int.TryParse(maxSessions, out var parsedMaxSessions) && parsedMaxSessions > 0)
        {
            config.MaxConcurrentSessions = parsedMaxSessions;
        }

        // Operation timeout
        var opTimeout = Environment.GetEnvironmentVariable("SHARPDBG_OPERATION_TIMEOUT_SECONDS");
        if (int.TryParse(opTimeout, out var parsedOpTimeout) && parsedOpTimeout > 0)
        {
            config.OperationTimeoutSeconds = parsedOpTimeout;
        }

        // Allow other user processes
        var allowOther = Environment.GetEnvironmentVariable("SHARPDBG_ALLOW_OTHER_USER_PROCESSES");
        if (bool.TryParse(allowOther, out var parsedAllowOther))
        {
            config.AllowOtherUserProcesses = parsedAllowOther;
        }

        // Expression evaluation timeout
        var evalTimeout = Environment.GetEnvironmentVariable("SHARPDBG_EVAL_TIMEOUT_MS");
        if (int.TryParse(evalTimeout, out var parsedEvalTimeout) && parsedEvalTimeout > 0)
        {
            config.ExpressionEvaluationTimeoutMs = parsedEvalTimeout;
        }

        // Breakpoint bind timeout
        var bindTimeout = Environment.GetEnvironmentVariable("SHARPDBG_BREAKPOINT_BIND_TIMEOUT_MS");
        if (int.TryParse(bindTimeout, out var parsedBindTimeout) && parsedBindTimeout > 0)
        {
            config.BreakpointBindTimeoutMs = parsedBindTimeout;
        }

        // Just my code
        var justMyCode = Environment.GetEnvironmentVariable("SHARPDBG_JUST_MY_CODE");
        if (bool.TryParse(justMyCode, out var parsedJustMyCode))
        {
            config.JustMyCode = parsedJustMyCode;
        }

        // Diagnostics
        var diagnostics = Environment.GetEnvironmentVariable("SHARPDBG_ENABLE_DIAGNOSTICS");
        if (bool.TryParse(diagnostics, out var parsedDiagnostics))
        {
            config.EnableDiagnostics = parsedDiagnostics;
        }

        return config;
    }

    /// <summary>
    /// Validate configuration and return error message if invalid
    /// </summary>
    public string? Validate()
    {
        if (MaxConcurrentSessions < 1)
            return "MaxConcurrentSessions must be at least 1";

        if (OperationTimeoutSeconds < 1)
            return "OperationTimeoutSeconds must be at least 1";

        if (ExpressionEvaluationTimeoutMs < 100)
            return "ExpressionEvaluationTimeoutMs must be at least 100ms";

        if (BreakpointBindTimeoutMs < 100)
            return "BreakpointBindTimeoutMs must be at least 100ms";

        return null;
    }
}
