using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Discovers .NET processes running on the system
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
            var allProcesses = Process.GetProcesses();

            foreach (var process in allProcesses)
            {
                try
                {
                    if (IsDotNetProcess(process.Id))
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

            // First, check the process name for common .NET runtime patterns
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

            // Try to check modules (may fail on macOS/Linux without permissions)
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
                // Windows. Fall back to the process name check.
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Process doesn't exist or we can't access it
            return false;
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

            if (!IsDotNetProcess(processId))
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

    private string? GetMainModulePath(Process process)
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
