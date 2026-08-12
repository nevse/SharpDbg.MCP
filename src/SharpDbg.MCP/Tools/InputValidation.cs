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

    /// <summary>
    /// Validates the program to launch and returns it as an absolute path. A path that does not
    /// exist fails deep inside the debugger with "Process start failed", and a project file is the
    /// mistake worth naming: what runs is build output, not a project.
    /// </summary>
    public static string ValidateProgramPath(string programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            throw new ArgumentException("Program path cannot be empty", nameof(programPath));

        if (Path.GetExtension(programPath) is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx")
            throw new ArgumentException(
                $"{programPath} is a project, not a program. Build it and pass the .dll or the " +
                "executable next to it, usually under bin/Debug/<framework>.",
                nameof(programPath));

        var fullPath = Path.GetFullPath(programPath);

        if (!File.Exists(fullPath))
            throw new ArgumentException($"There is no file at {fullPath}", nameof(programPath));

        return fullPath;
    }

    public static void ValidateWorkingDirectory(string? workingDirectory)
    {
        if (workingDirectory is null)
            return;

        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Working directory cannot be blank", nameof(workingDirectory));

        if (!Directory.Exists(workingDirectory))
            throw new ArgumentException($"There is no directory at {workingDirectory}", nameof(workingDirectory));
    }

    public static void ValidateOutputLineCount(int maxLines)
    {
        if (maxLines <= 0)
            throw new ArgumentException($"max_lines must be positive, got: {maxLines}", nameof(maxLines));
    }

    /// <summary>
    /// Validates a function breakpoint name. Only emptiness is checked here: the pattern grammar is
    /// the debugger's, and it reports a bad pattern as an unverified breakpoint with the reason,
    /// which is more useful than a second opinion from here.
    /// </summary>
    public static void ValidateFunctionName(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Function name cannot be empty", nameof(functionName));
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
