using System.ComponentModel;
using System.Diagnostics;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Discovers .NET processes running on the system.
/// A process is recognised by the diagnostic IPC endpoint the runtime publishes, which is exact and
/// says nothing about what the executable is called - so a self-contained or single-file app is found
/// as readily as one launched through `dotnet`. Matching on the process name and loaded modules is
/// kept as a fallback, because a process started with diagnostics switched off publishes no endpoint
/// while still being perfectly debuggable.
/// </summary>
public class ProcessDiscovery
{
    /// <summary>
    /// List all .NET processes currently running
    /// </summary>
    public List<ProcessInfo> ListDotNetProcesses()
    {
        var dotnetProcesses = new List<ProcessInfo>();

        try
        {
            var endpoints = DiagnosticEndpoints.Enumerate();

            // One enumeration, and the Process objects it hands back are the ones used to read the
            // name and the main module: asking the operating system again per candidate was pure cost
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (IsDotNetProcess(process, endpoints))
                    {
                        dotnetProcesses.Add(new ProcessInfo(
                            process.Id,
                            process.ProcessName,
                            GetMainModulePath(process),
                            ProcessOwnership.Of(process.Id)));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                               or Win32Exception)
                {
                    // Skip processes we can't access (permission denied or process exited)
                    Console.Error.WriteLine($"[ProcessDiscovery] Cannot access process {process.Id}: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException
                                       or Win32Exception)
        {
            // Return empty list if we can't enumerate processes
            Console.Error.WriteLine($"[ProcessDiscovery] Cannot enumerate processes: {ex.Message}");
        }

        return dotnetProcesses;
    }

    /// <summary>
    /// Check if a specific process is a .NET process
    /// </summary>
    public bool IsDotNetProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            return IsDotNetProcess(process, DiagnosticEndpoints.Enumerate());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Process doesn't exist or we can't access it
            return false;
        }
    }

    private static bool IsDotNetProcess(Process process, Dictionary<int, long> endpoints)
    {
        // Exact: the runtime published an endpoint for this very process
        if (DiagnosticEndpoints.Published(endpoints, process))
            return true;

        // A process with diagnostics disabled publishes nothing, so fall back to what it looks like
        var processName = process.ProcessName?.ToLowerInvariant();
        if (processName != null)
        {
            if (processName.Contains("dotnet") ||
                processName.Contains("testhost") ||
                processName == "vstest.console" ||
                processName.EndsWith(".dll"))
            {
                return true;
            }
        }

        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var moduleName = module.ModuleName?.ToLowerInvariant();
                if (moduleName != null &&
                    (moduleName.Contains("coreclr") ||
                     moduleName.Contains("clr.dll") ||
                     moduleName.Contains("libcoreclr")))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException
                                       or Win32Exception)
        {
            // Modules cannot be enumerated without being able to open the process: permissions on
            // macOS and Linux, "Unable to enumerate the process modules" as a Win32Exception on
            // Windows. There is nothing else left to check.
        }

        return false;
    }

    /// <summary>
    /// Get information about a specific process
    /// </summary>
    public ProcessInfo? GetProcessInfo(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            if (!IsDotNetProcess(process, DiagnosticEndpoints.Enumerate()))
                return null;

            return new ProcessInfo(
                process.Id,
                process.ProcessName,
                GetMainModulePath(process),
                ProcessOwnership.Of(processId));
        }
        catch (ArgumentException)
        {
            // Process with specified ID not found
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                       or Win32Exception)
        {
            // Can't access process or process exited
            Console.Error.WriteLine($"[ProcessDiscovery] Cannot access process {processId}: {ex.Message}");
            return null;
        }
    }

    private static string? GetMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException
                                       or Win32Exception)
        {
            // Can't access main module (permissions or platform limitation)
            return null;
        }
    }
}

/// <summary>
/// Information about a .NET process
/// </summary>
public record ProcessInfo(
    int ProcessId,
    string ProcessName,
    string? MainModule,
    ProcessOwnership.Ownership Owner = ProcessOwnership.Ownership.Unknown);
