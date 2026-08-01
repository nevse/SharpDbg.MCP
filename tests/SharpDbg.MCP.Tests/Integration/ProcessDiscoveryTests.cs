using System.Diagnostics;

using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tests.Integration;

/// <summary>
/// Process discovery against real processes. Marked as integration because it starts one: the point
/// of the change being tested is a process the old name matching could not recognise, and that needs
/// a real apphost rather than something launched through `dotnet`.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class ProcessDiscoveryTests
{
    /// <summary>
    /// The apphost the build produces next to the dll. Running it gives a process called
    /// SharpDbg.MCP.TestApp, which is what a self-contained or single-file app looks like: nothing in
    /// the name says .NET.
    /// </summary>
    private static string ApphostPath =>
        TestPaths.TestAppAssembly[..^".dll".Length] + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

    private static Process StartApphost()
    {
        var startInfo = new ProcessStartInfo(ApphostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the apphost");

        // Drain, or a full pipe would block it, and give the runtime time to publish its endpoint
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// The case the name matching missed: a .NET process whose executable is not called dotnet. It is
    /// found through the diagnostic endpoint the runtime publishes.
    /// </summary>
    [TestMethod]
    public void IsDotNetProcess_ForAnApphostThatIsNotCalledDotnet_IsRecognised()
    {
        Assert.IsTrue(File.Exists(ApphostPath), $"The build should produce an apphost at {ApphostPath}");

        var apphost = StartApphost();

        try
        {
            Assert.IsTrue(
                DebuggeeProcess.SpinUntil(() => new ProcessDiscovery().IsDotNetProcess(apphost.Id), TimeSpan.FromSeconds(30)),
                "A running .NET apphost was not recognised as a .NET process");

            // And it really is a name the old matching could not have caught
            Assert.DoesNotContain("dotnet", apphost.ProcessName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("testhost", apphost.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Kill(apphost);
        }
    }

    [TestMethod]
    public void ListDotNetProcesses_IncludesAnApphostAndTheTestHost()
    {
        var apphost = StartApphost();

        try
        {
            var found = DebuggeeProcess.SpinUntil(
                () => new ProcessDiscovery().ListDotNetProcesses().Any(p => p.ProcessId == apphost.Id),
                TimeSpan.FromSeconds(30));

            Assert.IsTrue(found, "The apphost should be listed");

            var processes = new ProcessDiscovery().ListDotNetProcesses();
            Assert.Contains(Environment.ProcessId, processes.Select(p => p.ProcessId).ToList(), "The test host is a .NET process too");
            Assert.IsTrue(
                processes.All(p => p.Owner == ProcessOwnership.Ownership.CurrentUser
                                   || p.Owner == ProcessOwnership.Ownership.OtherUser
                                   || p.Owner == ProcessOwnership.Ownership.Unknown),
                "Every entry carries an ownership answer");
        }
        finally
        {
            Kill(apphost);
        }
    }

    /// <summary>
    /// On Unix the endpoint sockets are left behind when a process dies - thousands of them pile up -
    /// so a dead process must not be reported as a live .NET one just because its socket is still
    /// there.
    /// </summary>
    [TestMethod]
    public void IsDotNetProcess_ForAProcessThatHasExited_IsFalse()
    {
        var apphost = StartApphost();
        var pid = apphost.Id;

        Assert.IsTrue(
            DebuggeeProcess.SpinUntil(() => new ProcessDiscovery().IsDotNetProcess(pid), TimeSpan.FromSeconds(30)),
            "Precondition: the apphost should be recognised while it runs");

        Kill(apphost);

        Assert.IsFalse(new ProcessDiscovery().IsDotNetProcess(pid), "A process that has exited is not a .NET process");
    }

    /// <summary>
    /// The reason the start time in the endpoint name is checked rather than the id alone. Unix keeps
    /// the socket file after the process dies - there were 2909 of them and 63 live ones on the
    /// machine this was written on - so a process that inherits one of those ids would otherwise be
    /// reported as a .NET process. A file with a matching name is enough to stand in for a leftover
    /// socket, since only the name is ever read.
    /// </summary>
    [TestMethod]
    public void IsDotNetProcess_WithALeftoverEndpointForARecycledId_IsNotFooled()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows publishes named pipes, which the operating system removes with the process");

        using var plain = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "30" } })
            ?? throw new InvalidOperationException("Could not start a process to stand in for a recycled id");

        var startTime = new DateTimeOffset(plain.StartTime).ToUnixTimeSeconds();
        var stale = Path.Combine(Path.GetTempPath(), $"dotnet-diagnostic-{plain.Id}-{startTime - 9999}-socket");
        var matching = Path.Combine(Path.GetTempPath(), $"dotnet-diagnostic-{plain.Id}-{startTime}-socket");

        try
        {
            File.WriteAllText(stale, string.Empty);

            Assert.IsFalse(
                new ProcessDiscovery().IsDotNetProcess(plain.Id),
                "/bin/sleep was called a .NET process because an endpoint from an earlier owner of its id was lying around");

            // The control: same process, same setup, only the start time now matches
            File.Delete(stale);
            File.WriteAllText(matching, string.Empty);

            Assert.IsTrue(
                new ProcessDiscovery().IsDotNetProcess(plain.Id),
                "An endpoint whose start time matches is what recognition rests on");
        }
        finally
        {
            File.Delete(stale);
            File.Delete(matching);
            Kill(plain);
        }
    }

    [TestMethod]
    public void GetProcessInfo_ForOurOwnProcess_DescribesIt()
    {
        var info = new ProcessDiscovery().GetProcessInfo(Environment.ProcessId);

        Assert.IsNotNull(info);
        Assert.AreEqual(Environment.ProcessId, info.ProcessId);
        Assert.AreEqual(ProcessOwnership.Ownership.CurrentUser, info.Owner);
    }
}
