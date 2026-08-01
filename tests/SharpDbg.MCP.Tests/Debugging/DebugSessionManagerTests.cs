using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tests.Debugging;

/// <summary>
/// A session only talks to the debugger once it attaches, so the manager's rules can be checked
/// without a debuggee.
/// </summary>
[TestClass]
public class DebugSessionManagerTests
{
    private static DebugSessionManager Manager(int maxSessions = 1) =>
        new(new ServerConfiguration { MaxConcurrentSessions = maxSessions });

    [TestMethod]
    public void Resolve_WithoutAnId_CreatesTheFirstSessionAndThenReturnsIt()
    {
        using var manager = Manager();

        var first = manager.Resolve(null);
        var again = manager.Resolve(null);

        Assert.AreSame(first, again, "Omitting the id must not keep making new sessions");
        Assert.HasCount(1, manager.GetAllSessions());
    }

    [TestMethod]
    public void Resolve_WithAKnownId_ReturnsThatSession()
    {
        using var manager = Manager(maxSessions: 2);

        var first = manager.CreateSession();
        var second = manager.CreateSession();

        Assert.AreSame(first, manager.Resolve(first.SessionId));
        Assert.AreSame(second, manager.Resolve(second.SessionId));
    }

    [TestMethod]
    public void Resolve_WithAnUnknownId_SaysSoAndPointsAtListSessions()
    {
        using var manager = Manager();

        var failure = Assert.ThrowsExactly<ArgumentException>(() => manager.Resolve(42));

        Assert.Contains("no debug session with id 42", failure.Message);
        Assert.Contains("list_sessions", failure.Message);
    }

    /// <summary>
    /// Guessing which process a continue was meant for is worse than asking, so with more than one
    /// session open the id becomes required.
    /// </summary>
    [TestMethod]
    public void Resolve_WithoutAnIdWhileSeveralAreOpen_RefusesToGuess()
    {
        using var manager = Manager(maxSessions: 3);

        manager.CreateSession();
        manager.CreateSession();

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => manager.Resolve(null));

        Assert.Contains("session_id is required", failure.Message);
    }

    [TestMethod]
    public void AcquireForAttach_ReusesASessionThatIsNotAttached()
    {
        using var manager = Manager();

        var existing = manager.Resolve(null);

        // Nothing is attached, so a second attach must not need another slot
        Assert.AreSame(existing, manager.AcquireForAttach(null));
        Assert.HasCount(1, manager.GetAllSessions());
    }

    [TestMethod]
    public void CreateSession_PastTheLimit_SaysHowToFreeASlot()
    {
        using var manager = Manager();

        manager.CreateSession();

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => manager.CreateSession());

        Assert.Contains("Maximum number of concurrent debug sessions (1)", failure.Message);
        Assert.Contains("close_session", failure.Message);
        Assert.Contains("SHARPDBG_MAX_SESSIONS", failure.Message);
    }

    [TestMethod]
    public void CloseSession_FreesTheSlotAndForgetsTheSession()
    {
        using var manager = Manager();

        var session = manager.CreateSession();
        manager.CloseSession(session.SessionId);

        Assert.IsEmpty(manager.GetAllSessions());

        // The slot is free again
        var replacement = manager.CreateSession();
        Assert.AreNotEqual(session.SessionId, replacement.SessionId, "Ids must not be reused");
    }

    [TestMethod]
    public void CloseSession_WithAnUnknownId_DoesNothing()
    {
        using var manager = Manager();

        manager.CreateSession();
        manager.CloseSession(999);

        Assert.HasCount(1, manager.GetAllSessions());
    }

    [TestMethod]
    public void Members_AfterDispose_Throw()
    {
        var manager = Manager();
        manager.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.Resolve(null));
        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.CreateSession());
        Assert.ThrowsExactly<ObjectDisposedException>(() => manager.GetAllSessions());
    }
}
