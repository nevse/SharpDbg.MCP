using System.Diagnostics;
using System.Globalization;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// The diagnostic IPC endpoints the .NET runtime publishes, which is how `dotnet-trace ps` knows
/// what is a .NET process. Unlike matching on a process name it does not care what the executable is
/// called, so a self-contained or single-file app is found too.
///
/// The endpoint is named `dotnet-diagnostic-{pid}-{startTime}` - a socket in the temp directory on
/// Unix, a named pipe on Windows. On Unix the socket file is left behind when the process dies, and
/// there were 2909 of them on the machine this was written on, only 63 of them live. A name on its
/// own therefore proves nothing: the pid has to belong to a running process, and the start time in
/// the name has to match it, or a pid that has since been reused would look like a .NET process.
/// </summary>
internal static class DiagnosticEndpoints
{
    private const string Prefix = "dotnet-diagnostic-";

    /// <summary>
    /// Start times are whole seconds in the endpoint name, so a match is allowed to be a second out
    /// </summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Every published endpoint, as process id to the start time recorded in its name. Both are
    /// needed: the id alone cannot tell a live process from a leftover socket.
    /// </summary>
    public static Dictionary<int, long> Enumerate()
    {
        var endpoints = new Dictionary<int, long>();

        try
        {
            var directory = OperatingSystem.IsWindows() ? @"\\.\pipe\" : Path.GetTempPath();

            foreach (var path in Directory.EnumerateFiles(directory, Prefix + "*"))
            {
                if (TryParse(Path.GetFileName(path), out var processId, out var startTime))
                    endpoints[processId] = startTime;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or DirectoryNotFoundException)
        {
            // No endpoints we can see, so nothing is exact - callers fall back to their own guess
        }

        return endpoints;
    }

    /// <summary>
    /// Whether a process published one of these endpoints, and it is that process rather than an
    /// earlier one that happened to have the same id.
    /// </summary>
    public static bool Published(Dictionary<int, long> endpoints, Process process)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(process);

        if (!endpoints.TryGetValue(process.Id, out var startTime))
            return false;

        try
        {
            var difference = new DateTimeOffset(process.StartTime).ToUnixTimeSeconds() - startTime;

            return Math.Abs(difference) <= (long)StartTimeTolerance.TotalSeconds;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException)
        {
            // The start time is not readable for processes we cannot open, which are another user's
            // and refused at attach anyway. The endpoint is still the best evidence available.
            return true;
        }
    }

    private static bool TryParse(string fileName, out int processId, out long startTime)
    {
        processId = 0;
        startTime = 0;

        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // dotnet-diagnostic-{pid}-{startTime}-socket on Unix, without the suffix on Windows
        var parts = fileName[Prefix.Length..].Split('-');

        return parts.Length >= 2
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out processId)
            && long.TryParse(parts[1], CultureInfo.InvariantCulture, out startTime);
    }
}
