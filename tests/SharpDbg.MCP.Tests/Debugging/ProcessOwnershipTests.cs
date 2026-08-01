using SharpDbg.MCP.Debugging;

using static SharpDbg.MCP.Debugging.ProcessOwnership.Ownership;

namespace SharpDbg.MCP.Tests.Debugging;

[TestClass]
public class ProcessOwnershipTests
{
    /// <summary>
    /// The one case that must work everywhere: this test process is ours. Each platform has its own
    /// way of answering that - /proc on Linux, ps on macOS, the process token on Windows - so this
    /// running green on all three in CI is what says every path works.
    /// </summary>
    [TestMethod]
    public void Of_OurOwnProcess_IsTheCurrentUser()
    {
        Assert.AreEqual(CurrentUser, ProcessOwnership.Of(Environment.ProcessId));
    }

    [TestMethod]
    public void Of_AProcessThatDoesNotExist_IsUnknown()
    {
        // Above the pid range any platform hands out, so nothing can own it
        Assert.AreEqual(Unknown, ProcessOwnership.Of(int.MaxValue - 1));
    }

    /// <summary>
    /// A process we must not be allowed to attach to by default. On Unix pid 1 belongs to root; on
    /// Windows pid 4 is System, whose token cannot be opened at all, so the answer there is Unknown
    /// rather than OtherUser - both refuse.
    /// </summary>
    [TestMethod]
    public void Of_ASystemProcess_IsNotReportedAsOurs()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            Assert.Inconclusive("Running as root, so pid 1 really is ours");

        var ownership = ProcessOwnership.Of(OperatingSystem.IsWindows() ? 4 : 1);

        Assert.AreNotEqual(CurrentUser, ownership,
            "A process owned by root or SYSTEM must never be reported as the current user's");
    }

    [TestMethod]
    public void DenyReason_OwnProcess_IsAllowedEitherWay()
    {
        Assert.IsNull(ProcessOwnership.DenyReason(CurrentUser, allowOtherUserProcesses: false));
        Assert.IsNull(ProcessOwnership.DenyReason(CurrentUser, allowOtherUserProcesses: true));
    }

    [TestMethod]
    public void DenyReason_WithTheSettingOn_AllowsAnything()
    {
        Assert.IsNull(ProcessOwnership.DenyReason(OtherUser, allowOtherUserProcesses: true));
        Assert.IsNull(ProcessOwnership.DenyReason(Unknown, allowOtherUserProcesses: true));
    }

    /// <summary>
    /// An owner we could not establish has to be refused as well. Treating it as ours would make the
    /// setting decorative on any platform or process where the lookup does not work.
    /// </summary>
    [TestMethod]
    public void DenyReason_WithTheSettingOff_RefusesOtherUsersAndUnknownOwners()
    {
        var other = ProcessOwnership.DenyReason(OtherUser, allowOtherUserProcesses: false);
        var unknown = ProcessOwnership.DenyReason(Unknown, allowOtherUserProcesses: false);

        Assert.IsNotNull(other);
        Assert.Contains("another user", other);
        Assert.Contains("SHARPDBG_ALLOW_OTHER_USER_PROCESSES", other, "The caller needs to know how to override it");

        Assert.IsNotNull(unknown);
        Assert.Contains("could not be determined", unknown);
    }

    /// <summary>
    /// Regression: on Windows, reading the modules of a process that cannot be opened throws
    /// Win32Exception rather than UnauthorizedAccessException, which no catch filter covered, so a
    /// single unreadable process - a system or other user's one, of which there are always some -
    /// made the whole listing throw.
    /// </summary>
    [TestMethod]
    public void ListDotNetProcesses_DoesNotThrowOnProcessesItCannotOpen()
    {
        var processes = new ProcessDiscovery().ListDotNetProcesses();

        Assert.IsNotEmpty(processes, "This test host is a .NET process, so the list cannot be empty");
    }

    /// <summary>
    /// The process list is what a caller picks a pid from, so it has to carry the ownership: our own
    /// test host is a .NET process and must be listed as ours.
    /// </summary>
    [TestMethod]
    public void ListDotNetProcesses_ReportsOwnershipForOurOwnProcess()
    {
        var processes = new ProcessDiscovery().ListDotNetProcesses();

        var self = processes.SingleOrDefault(p => p.ProcessId == Environment.ProcessId);

        Assert.IsNotNull(self, "The test host is a .NET process, so it should be in the list");
        Assert.AreEqual(CurrentUser, self.Owner);
    }
}
