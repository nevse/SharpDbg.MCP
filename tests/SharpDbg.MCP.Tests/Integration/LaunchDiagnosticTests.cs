using System.Diagnostics;
using System.Runtime.InteropServices;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// TEMPORARY. Not a test of behaviour - a probe for why launching hangs on the macOS CI runner while
/// Linux, Windows and macOS locally are all green, including on the runner's exact SDK and runtime.
///
/// The first version of this probe disproved the reading the CI log invites. Two launches of the
/// assembly succeed on the runner, so it is not "the first works and the rest hang". Ordering the
/// failures by what they launch points at the apphost instead: it is the first to fail, and every
/// launch after it fails too.
///
/// The hang is inside ClrDebugExtensions.Automatic, between "Process created suspended" and the
/// attach: either DiagnosticClientResumeRuntime never returns, or the runtime-startup callback never
/// fires. Both are launch-only, which is why attach is unaffected. That is upstream code this
/// repository cannot log inside, so the probe reads the state of the world instead:
///
///   debuggee alive and stopped, no diagnostic socket -> the runtime never opened its port, so the
///                                                       resume had nothing to talk to
///   debuggee alive and a socket present              -> the resume landed and the callback is lost
///   debuggee gone                                    -> it died on startup
///
/// It ends in Assert.Fail on purpose: the runner's console logger prints a test's captured output
/// only when it fails, and stderr is the stream that survives.
///
/// Delete this file once the cause is known.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class LaunchDiagnosticTests
{
    /// <summary>
    /// The first probe disproved the obvious reading of the CI log. Two launches of the assembly
    /// succeed on the runner, so it is not "the first works and the rest hang". What the ordering
    /// actually shows is that the apphost launch is the first to fail and everything after it fails
    /// too, so this runs assembly, apphost, assembly and reports each.
    /// </summary>
    [TestMethod]
    public void Diagnostic_AssemblyThenApphostThenAssembly()
    {
        Report("before any launch");

        Launch("assembly (baseline)", TestPaths.TestAppAssembly);
        Launch("apphost (suspect)", TestPaths.TestAppExecutable);
        Launch("assembly again (poisoned?)", TestPaths.TestAppAssembly);

        Report("after all three");

        Assert.Fail("Probe, not a test - read the PROBE lines above");
    }

    private static void Log(string message) => Console.Error.WriteLine($"PROBE {message}");

    private static void Launch(string what, string program)
    {
        Log($"=== launching {what}: {program}");
        Log($"  exists={File.Exists(program)}");

        if (!OperatingSystem.IsWindows() && File.Exists(program))
            Log($"  ls: {RunAndCapture("ls", $"-l@ {program}").Trim()}");

        try
        {
            using var session = new DebugSession(1, new ServerConfiguration());
            session.Launch(program).GetAwaiter().GetResult();

            var stopwatch = Stopwatch.StartNew();
            try
            {
                session.Start();
                Log($"  STARTED in {stopwatch.ElapsedMilliseconds}ms");
                Report($"after {what} started");
            }
            catch (Exception ex)
            {
                Log($"  FAILED after {stopwatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                Report($"at the hang of {what}");
            }
        }
        catch (Exception ex)
        {
            Log($"  session error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Report(string when)
    {
        Log($" --- {when} ---");
        Log($" TMPDIR={Environment.GetEnvironmentVariable("TMPDIR") ?? "<unset>"}");

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

            Log($" debuggee: {state.Trim()}");
            found++;
        }

        if (found == 0)
            Log(" debuggee: none alive");
    }

    private static void ReportDiagnosticSockets()
    {
        var directory = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";

        try
        {
            var sockets = Directory.GetFiles(directory, "dotnet-diagnostic-*");
            Log($" diagnostic sockets in TMPDIR: {sockets.Length}");

            // Only the newest few matter; a runner accumulates them across the suite
            foreach (var socket in sockets.OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
                Log($"   {Path.GetFileName(socket)} {File.GetLastWriteTimeUtc(socket):HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Log($" diagnostic sockets: could not list - {ex.Message}");
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
