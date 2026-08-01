using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tools;

/// <summary>
/// Input validation helpers for MCP tools
/// </summary>
public static class InputValidation
{
    public static void ValidateProcessId(int processId)
    {
        if (processId <= 0)
            throw new ArgumentException($"Process ID must be positive, got: {processId}", nameof(processId));
    }

    public static void ValidateThreadId(int threadId)
    {
        if (threadId < 0)
            throw new ArgumentException($"Thread ID must be non-negative, got: {threadId}", nameof(threadId));
    }

    public static void ValidateFrameId(int frameId)
    {
        if (frameId <= 0)
            throw new ArgumentException($"Frame ID must be positive, got: {frameId}", nameof(frameId));
    }

    public static void ValidateVariablesReference(int variablesReference)
    {
        if (variablesReference <= 0)
            throw new ArgumentException(
                $"Variables reference must be positive, got: {variablesReference}", nameof(variablesReference));
    }

    public static void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
    }

    public static void ValidateLineNumber(int line)
    {
        if (line <= 0)
            throw new ArgumentException($"Line number must be positive, got: {line}", nameof(line));
    }

    public static void ValidateBreakpointId(int breakpointId)
    {
        if (breakpointId <= 0)
            throw new ArgumentException($"Breakpoint ID must be positive, got: {breakpointId}", nameof(breakpointId));
    }

    /// <summary>
    /// Validates a hit-count condition. The debugger silently treats an unparseable one as never
    /// satisfied, which would leave a breakpoint that never fires and no indication why.
    /// </summary>
    public static void ValidateHitCondition(string? hitCondition)
    {
        if (string.IsNullOrWhiteSpace(hitCondition))
            return;

        var text = hitCondition.Trim();

        var operatorLength = text switch
        {
            _ when text.StartsWith("==", StringComparison.Ordinal) => 2,
            _ when text.StartsWith(">=", StringComparison.Ordinal) => 2,
            _ when text.StartsWith("<=", StringComparison.Ordinal) => 2,
            _ when text[0] is '>' or '<' or '%' => 1,
            _ => 0
        };

        if (!int.TryParse(text[operatorLength..], out var count))
            throw new ArgumentException(
                "Hit condition must be a count, optionally prefixed with ==, >=, <=, >, < or %, " +
                $"got: {hitCondition}",
                nameof(hitCondition));

        if (text[0] == '%' && count <= 0)
            throw new ArgumentException(
                $"A '%' hit condition needs a positive interval, got: {hitCondition}",
                nameof(hitCondition));
    }

    /// <summary>
    /// Validates a blocking wait. The upper bound keeps a tool call from outliving the client's
    /// own request timeout, which would leave the caller with no result at all.
    /// </summary>
    public static void ValidateWaitTimeout(int timeoutMs)
    {
        const int MaxTimeoutMs = 300_000;

        if (timeoutMs <= 0)
            throw new ArgumentException($"Timeout must be positive, got: {timeoutMs}", nameof(timeoutMs));

        if (timeoutMs > MaxTimeoutMs)
            throw new ArgumentException(
                $"Timeout must be at most {MaxTimeoutMs}ms, got: {timeoutMs}", nameof(timeoutMs));
    }

    /// <summary>
    /// Parses an exception break mode, naming the accepted values on failure - an unrecognised mode
    /// silently falling back to a default would change whether the debuggee stops.
    /// </summary>
    public static ExceptionBreakMode ParseExceptionBreakMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("Exception break mode cannot be empty", nameof(mode));

        return mode.Trim().ToLowerInvariant() switch
        {
            "always" => ExceptionBreakMode.Always,
            "never" => ExceptionBreakMode.Never,
            _ => throw new ArgumentException(
                $"Exception break mode must be 'always' or 'never', got: {mode}", nameof(mode))
        };
    }

    public static void ValidateExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Expression cannot be empty", nameof(expression));
    }
}
