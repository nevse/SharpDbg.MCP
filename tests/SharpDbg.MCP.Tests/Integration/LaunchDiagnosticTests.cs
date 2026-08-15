using System.Diagnostics;
using System.Runtime.InteropServices;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// TEMPORARY. Not a test of behaviour - a probe for why the second launch in a test host hangs on
/// the macOS CI runner while the first succeeds, and while everything passes on Linux, on Windows,
/// and locally on macOS including on the runner's exact SDK and runtime.
///
/// The hang is inside ClrDebugExtensions.Automatic, between "Process created suspended" and the
/// attach: either DiagnosticClientResumeRuntime never returns, or the runtime-startup callback never
/// fires. Both are launch-only, which is why attach is unaffected. This cannot be logged from here,
/// so it reads the state of the world instead, which separates the two:
///
///   debuggee alive and stopped, no diagnostic socket -> the runtime never opened its port, so the
///                                                       resume had nothing to talk to
///   debuggee alive and a socket present              -> the resume landed and the callback is lost
///   debuggee gone                                    -> it died on startup
///
/// Delete this file once the cause is known.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class LaunchDiagnosticTests
{
    [TestMethod]
    public async Task Diagnostic_TwoLaunchesInOneHost()
    {
        Report("before any launch");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var session = new DebugSession(1, new ServerConfiguration());
            await session.Launch(TestPaths.TestAppAssembly);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                session.Start();
                Console.WriteLine($"PROBE launch {attempt}: started in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PROBE launch {attempt}: FAILED after {stopwatch.ElapsedMilliseconds}ms");
                Console.WriteLine($"PROBE   {ex.GetType().Name}: {ex.Message}");
                Report($"at the hang of launch {attempt}");
                throw;
            }

            Report($"after launch {attempt} started");
        }

        Report("after both sessions were disposed");
    }

    private static void Report(string when)
    {
        Console.WriteLine($"PROBE --- {when} ---");
        Console.WriteLine($"PROBE TMPDIR={Environment.GetEnvironmentVariable("TMPDIR") ?? "<unset>"}");

        ReportDebuggees();
        ReportDiagnosticSockets();
    }

    private static void ReportDebuggees()
    {
        var found = 0;

        foreach (var process in Process.GetProcesses())
        {
            string name;
            try { name = process.ProcessName; }
            catch { continue; }

            if (!name.Contains("TestApp", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                continue;

            // A dotnet host is only interesting if it is one we launched, which the state line shows
            var state = RunAndCapture("ps", $"-o pid=,stat=,command= -p {process.Id}");
            if (!state.Contains("TestApp", StringComparison.OrdinalIgnoreCase))
                continue;

            Console.WriteLine($"PROBE debuggee: {state.Trim()}");
            found++;
        }

        if (found == 0)
            Console.WriteLine("PROBE debuggee: none alive");
    }

    private static void ReportDiagnosticSockets()
    {
        var directory = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";

        try
        {
            var sockets = Directory.GetFiles(directory, "dotnet-diagnostic-*");
            Console.WriteLine($"PROBE diagnostic sockets in TMPDIR: {sockets.Length}");

            // Only the newest few matter; a runner accumulates them across the suite
            foreach (var socket in sockets.OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
                Console.WriteLine($"PROBE   {Path.GetFileName(socket)} {File.GetLastWriteTimeUtc(socket):HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PROBE diagnostic sockets: could not list - {ex.Message}");
        }
    }

    private static string RunAndCapture(string file, string arguments)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "(no ps on Windows)";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
                return string.Empty;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex)
        {
            return $"({ex.Message})";
        }
    }
}
