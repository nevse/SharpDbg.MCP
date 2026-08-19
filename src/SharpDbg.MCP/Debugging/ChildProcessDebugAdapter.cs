using System.Diagnostics;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Starts the debug adapter as a child process and hands back the two streams a client talks to it
/// through - its standard input and standard output, which is the transport DAP was designed for.
///
/// The alternative is hosting the adapter in this process, which is what this server did while it was
/// built on SharpDbg. A child process costs a process and the work of finding its binary, and buys two
/// things worth more than that: the debugging shim segfaults inside libmscordbi often enough to have
/// killed test runs, and in a child that kills the child rather than this server; and it is how the
/// adapter is meant to be run, so nothing here depends on it also being usable as a library.
///
/// Launched through the muxer rather than through its apphost, and deliberately: on macOS a debugger
/// has to be allowed to take the debuggee's task port, the apphost the SDK produces carries no
/// entitlements, and the muxer does. That is the same refusal-by-hanging that cost a day on the test
/// app - see the macOS section of the README.
/// </summary>
internal static class ChildProcessDebugAdapter
{
    /// <summary>Overrides where the adapter is looked for, for a build that puts it somewhere else</summary>
    private const string PathVariable = "SHARPDBG_ADAPTER_PATH";

    public static (Stream Input, Stream Output, AdapterProcess Adapter) Start(Action<string>? logger = null)
    {
        var adapterDll = ResolveAdapter();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Its own directory, so the logs it writes beside itself land somewhere predictable
            WorkingDirectory = Path.GetDirectoryName(adapterDll)!
        };
        startInfo.ArgumentList.Add(adapterDll);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the debug adapter: {adapterDll}");

        // Drained rather than ignored: a full stderr pipe would block the adapter, and it would look
        // exactly like a debugger that has stopped answering
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                logger?.Invoke($"[adapter] {line}");
        });

        return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            new AdapterProcess(process, logger));
    }

    /// <summary>
    /// The adapter ships beside this assembly, under clrdbg/. The environment variable is for a
    /// developer pointing at a build of their own.
    /// </summary>
    private static string ResolveAdapter()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException(
                    $"{PathVariable} points at {configured}, which does not exist.", configured);
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "clrdbg", "clrdbg.dll");

        return File.Exists(beside)
            ? beside
            : throw new FileNotFoundException(
                $"The debug adapter was not found at {beside}. It is built and copied there by the "
                + $"BuildClrdbgAdapter target; set {PathVariable} to use one built elsewhere.", beside);
    }

    /// <summary>
    /// Ends the adapter by closing its standard input first, which is how a DAP adapter is told the
    /// conversation is over, and killing it only if that is not enough. The kill takes the tree: a
    /// launched debuggee is the adapter's child, so it would otherwise be left orphaned and suspended.
    /// </summary>
    internal sealed class AdapterProcess(Process process, Action<string>? logger) : IDisposable
    {
        /// <summary>
        /// The debugger's own process. Worth being able to name: this server now has a second process
        /// behind it, and when the debugger stops answering that is the one to look at. It is also what
        /// makes the isolation testable without going process-hunting through the OS.
        /// </summary>
        public int ProcessId { get; } = process.Id;

        private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();

                    if (!process.WaitForExit(ExitGrace))
                    {
                        logger?.Invoke(
                            $"The debug adapter did not exit within {ExitGrace.TotalSeconds:0}s of its "
                            + "input closing, so it is being killed");
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                // Already gone, or gone between the check and the call - either way there is nothing
                // left to release, and a teardown must not fail on it
                logger?.Invoke($"Failed to release the debug adapter process: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
