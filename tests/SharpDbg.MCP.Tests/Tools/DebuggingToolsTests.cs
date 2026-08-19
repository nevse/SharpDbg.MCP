using System.Text.Json;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;
using SharpDbg.MCP.Documentation;
using SharpDbg.MCP.Tools;

namespace SharpDbg.MCP.Tests.Tools;

/// <summary>
/// The tool layer used to be static singletons, which meant everything here could only be reached by
/// running the real server. These cover what a caller gets back before any process is involved:
/// rejected input, unknown session ids, and the not-attached answers.
/// </summary>
[TestClass]
public class DebuggingToolsTests
{
    private static DebuggingTools Tools(ServerConfiguration? configuration = null)
    {
        var config = configuration ?? new ServerConfiguration();

        return new DebuggingTools(config, new DebugSessionManager(config), new ProcessDiscovery());
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Error(string json)
    {
        var root = Parse(json);

        Assert.IsFalse(root.GetProperty("success").GetBoolean(), $"Expected a failure, got: {json}");
        return root.GetProperty("error").GetString()!;
    }

    [TestMethod]
    public void Constructor_WithoutItsDependencies_Throws()
    {
        var config = new ServerConfiguration();

        Assert.ThrowsExactly<ArgumentNullException>(() => new DebuggingTools(null!, new DebugSessionManager(config), new ProcessDiscovery()));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DebuggingTools(config, null!, new ProcessDiscovery()));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DebuggingTools(config, new DebugSessionManager(config), null!));
    }

    [TestMethod]
    public void GetProcessStatus_WithNoSessionYet_CreatesOneAndReportsNotAttached()
    {
        var root = Parse(Tools().GetProcessStatus());

        Assert.AreEqual(1, root.GetProperty("session_id").GetInt32());
        Assert.IsFalse(root.GetProperty("is_attached").GetBoolean());
        Assert.IsFalse(root.GetProperty("is_running").GetBoolean());
    }

    [TestMethod]
    public void GetProcessStatus_WithAnUnknownSessionId_SaysWhereToLook()
    {
        var error = Error(Tools().GetProcessStatus(session_id: 7));

        Assert.Contains("no debug session with id 7", error);
        Assert.Contains("list_sessions", error);
    }

    [TestMethod]
    public void ListSessions_BeforeAnythingHappens_IsEmptyAndReportsTheLimit()
    {
        var root = Parse(Tools(new ServerConfiguration { MaxConcurrentSessions = 3 }).ListSessions());

        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("count").GetInt32());
        Assert.AreEqual(3, root.GetProperty("max_sessions").GetInt32());
    }

    /// <summary>
    /// Listing must not be what creates a session, or the count would answer a different question
    /// than the one asked.
    /// </summary>
    [TestMethod]
    public void ListSessions_AfterAToolThatResolvesASession_ShowsIt()
    {
        var tools = Tools();

        tools.GetProcessStatus();

        var root = Parse(tools.ListSessions());
        Assert.AreEqual(1, root.GetProperty("count").GetInt32());
        Assert.AreEqual(1, root.GetProperty("sessions")[0].GetProperty("session_id").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("sessions")[0].GetProperty("process_id").ValueKind);
    }

    [TestMethod]
    public void CloseSession_WithAnUnknownId_SaysSo()
    {
        Assert.Contains("no debug session with id 4", Error(Tools().CloseSession(4)));
    }

    [TestMethod]
    public void WaitForStop_WithoutAttaching_SaysToAttachFirst()
    {
        Assert.Contains("attach_to_process", Error(Tools().WaitForStop()));
    }

    [TestMethod]
    public void WaitForStop_WithATimeoutPastTheMaximum_IsRejectedBeforeWaiting()
    {
        Assert.Contains("at most 300000ms", Error(Tools().WaitForStop(timeout_ms: 400_000)));
    }

    [TestMethod]
    public void SetBreakpoint_WithAnImpossibleLine_IsRejected()
    {
        Assert.Contains("Line number must be positive", Error(Tools().SetBreakpoint("/tmp/Program.cs", 0)));
    }

    [TestMethod]
    public void SetBreakpoint_WithAnUnparseableHitCondition_IsRejected()
    {
        var error = Error(Tools().SetBreakpoint("/tmp/Program.cs", 12, hit_condition: "every other"));

        Assert.Contains("Hit condition must be a count", error);
    }

    [TestMethod]
    public void SetFunctionBreakpoint_WithAnEmptyName_IsRejected()
    {
        Assert.Contains("Function name cannot be empty", Error(Tools().SetFunctionBreakpoint("  ")));
    }

    [TestMethod]
    public void SetExceptionBreakMode_WithAnUnknownMode_NamesTheValidOnes()
    {
        var error = Error(Tools().SetExceptionBreakMode("sometimes"));

        Assert.Contains("'always', 'user_unhandled', 'unhandled' or 'never'", error);
    }

    /// <summary>
    /// The two quiet modes report no exception, so a type list for them would silently do nothing.
    /// </summary>
    [TestMethod]
    public void SetExceptionBreakMode_WithTypesForAQuietMode_IsRejected()
    {
        var error = Error(Tools().SetExceptionBreakMode("unhandled", ["System.FormatException"]));

        Assert.Contains("cannot be used with mode 'unhandled'", error);
    }

    /// <summary>
    /// The debugger's filter is a comma-separated list with a leading '!' for exclusions, so a name
    /// carrying either would change what the list means rather than be matched.
    /// </summary>
    [TestMethod]
    public void SetExceptionBreakMode_WithACommaInAType_IsRejected()
    {
        var error = Error(Tools().SetExceptionBreakMode("always", ["System.IOException,System.FormatException"]));

        Assert.Contains("one per entry", error);
    }

    [TestMethod]
    public void SetExceptionBreakMode_WithALeadingBangInAType_PointsAtTheFlag()
    {
        var error = Error(Tools().SetExceptionBreakMode("always", ["!System.FormatException"]));

        Assert.Contains("types_are_excluded", error);
    }

    [TestMethod]
    public void SetExceptionBreakMode_WithAnEmptyType_IsRejected()
    {
        Assert.Contains("cannot be empty", Error(Tools().SetExceptionBreakMode("always", ["  "])));
    }

    [TestMethod]
    public void GetExceptionInfo_WithoutAttaching_SaysToAttachFirst()
    {
        Assert.Contains("attach_to_process", Error(Tools().GetExceptionInfo()));
    }

    [TestMethod]
    public void GetExceptionInfo_WithAnImpossibleThreadId_IsRejected()
    {
        Assert.Contains("Thread ID must be non-negative", Error(Tools().GetExceptionInfo(thread_id: -1)));
    }

    [TestMethod]
    public void ExpandVariable_WithAnImpossibleReference_IsRejected()
    {
        Assert.Contains("must be positive", Error(Tools().ExpandVariable(0)));
    }

    [TestMethod]
    public void AttachToProcess_WithAnImpossibleProcessId_IsRejected()
    {
        Assert.Contains("Process ID must be positive", Error(Tools().AttachToProcess(0)));
    }

    /// <summary>
    /// The ownership check runs before anything looks at the process, so it answers even for a pid
    /// that is not a .NET process at all - on Unix pid 1 belongs to root, and on Windows its owner
    /// cannot be read, both of which are refused.
    /// </summary>
    [TestMethod]
    public void AttachToProcess_ToAProcessThatIsNotOurs_IsRefused()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            Assert.Inconclusive("Running as root, so pid 1 really is ours");

        Assert.Contains("Refusing to attach", Error(Tools().AttachToProcess(1)));
    }

    [TestMethod]
    public void AttachToProcess_WithTheOwnerCheckOff_GetsPastItToTheDotNetCheck()
    {
        var tools = Tools(new ServerConfiguration { AllowOtherUserProcesses = true });

        // Which proves the setting is what the refusal depends on: the next check speaks instead
        Assert.Contains("not a .NET process", Error(tools.AttachToProcess(1)));
    }

    [TestMethod]
    public void ListDotNetProcesses_ReportsWhetherOnlyOwnedProcessesAreAttachable()
    {
        var restricted = Parse(Tools().ListDotNetProcesses());
        var open = Parse(Tools(new ServerConfiguration { AllowOtherUserProcesses = true }).ListDotNetProcesses());

        Assert.IsTrue(restricted.GetProperty("attachable_owners_only").GetBoolean());
        Assert.IsFalse(open.GetProperty("attachable_owners_only").GetBoolean());
    }

    /// <summary>
    /// The documentation tools have the same shape, and being constructible outside the host is the
    /// point of the change.
    /// </summary>
    [TestMethod]
    public void DocumentationTools_AnswerWithoutTheHost()
    {
        var loader = new DocumentationLoader();
        var tools = new McpTools(loader, new ConceptIndex(loader), new FlowDiagramProvider(loader));

        var root = Parse(tools.ExplainICorDebugInterface("ICorDebugEval"));

        Assert.AreEqual("ICorDebugEval", root.GetProperty("interface_name").GetString());
    }
}
