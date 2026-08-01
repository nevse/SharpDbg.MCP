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

    public static void ValidateExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Expression cannot be empty", nameof(expression));
    }
}
