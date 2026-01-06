using Microsoft.Extensions.Logging;

namespace SharpDbg.MCP.Logging;

/// <summary>
/// Centralized logging helper for MCP server operations
/// </summary>
public static class McpLogger
{
    private static ILogger? _logger;

    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    public static void LogToolInvocation(string toolName, string? parameters = null)
    {
        _logger?.LogInformation("Tool invoked: {ToolName} | Parameters: {Parameters}",
            toolName, parameters ?? "none");
    }

    public static void LogToolSuccess(string toolName, double durationMs)
    {
        _logger?.LogInformation("Tool completed: {ToolName} | Duration: {Duration}ms",
            toolName, durationMs);
    }

    public static void LogToolError(string toolName, Exception ex)
    {
        _logger?.LogError(ex, "Tool failed: {ToolName} | Error: {ErrorMessage}",
            toolName, ex.Message);
    }

    public static void LogDebugSessionEvent(int sessionId, string eventType, string details)
    {
        _logger?.LogInformation("Session {SessionId} | Event: {EventType} | {Details}",
            sessionId, eventType, details);
    }

    public static void LogDebugSessionError(int sessionId, string operation, Exception ex)
    {
        _logger?.LogError(ex, "Session {SessionId} | Operation: {Operation} | Error: {ErrorMessage}",
            sessionId, operation, ex.Message);
    }

    public static void LogWarning(string message, params object[] args)
    {
        _logger?.LogWarning(message, args);
    }

    public static void LogDebug(string message, params object[] args)
    {
        _logger?.LogDebug(message, args);
    }
}
