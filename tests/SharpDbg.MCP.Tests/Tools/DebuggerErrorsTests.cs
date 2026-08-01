using System.Runtime.InteropServices;
using System.Text.Json;

using ICorDebugSharp;

using SharpDbg.MCP.Tools;

namespace SharpDbg.MCP.Tests.Tools;

[TestClass]
public class DebuggerErrorsTests
{
    [TestMethod]
    public void Explain_KnownHResult_DescribesWhatToDoNext()
    {
        var explanation = DebuggerErrors.Explain(
            new COMException("Returned from a call to Continue that was not matched with a stopping event.",
                Cor.CORDBG_E_SUPERFLOUS_CONTINUE));

        Assert.IsNotNull(explanation);
        Assert.Contains("already running", explanation);
    }

    /// <summary>
    /// The failure that costs a session: the caller has to be told that retrying is pointless and
    /// detaching is the way out, which the raw COM message does not say.
    /// </summary>
    [TestMethod]
    public void Explain_DisposedHandle_SaysRetryingWillNotHelp()
    {
        var explanation = DebuggerErrors.Explain(
            new COMException("Handle has been disposed.", Cor.CORDBG_E_HANDLE_HAS_BEEN_DISPOSED));

        Assert.IsNotNull(explanation);
        Assert.Contains("detach_from_process", explanation);
    }

    [TestMethod]
    public void Explain_WrappedHResult_LooksAtInnerExceptions()
    {
        var wrapped = new InvalidOperationException(
            "Step failed",
            new COMException("The process has been terminated.", Cor.CORDBG_E_PROCESS_TERMINATED));

        var explanation = DebuggerErrors.Explain(wrapped);

        Assert.IsNotNull(explanation);
        Assert.Contains("exited", explanation);
    }

    /// <summary>
    /// Some failures only carry the HRESULT as text, because the wrapper kept its own HResult
    /// </summary>
    [TestMethod]
    public void Explain_HResultOnlyInTheMessage_IsStillRecognised()
    {
        var explanation = DebuggerErrors.Explain(
            new InvalidOperationException("Continue failed: Handle has been disposed. (0x80131C01)"));

        Assert.IsNotNull(explanation);
        Assert.Contains("detach_from_process", explanation);
    }

    [TestMethod]
    public void Explain_UnrelatedFailure_ReturnsNull()
    {
        Assert.IsNull(DebuggerErrors.Explain(new ArgumentException("Line number must be positive, got: 0")));
        Assert.IsNull(DebuggerErrors.Explain(new COMException("Something else entirely", -2147024809)));
        Assert.IsNull(DebuggerErrors.Explain(new InvalidOperationException("no hresult here (0xDEADBEEF)")));
    }

    [TestMethod]
    public void ErrorResponse_KeepsTheRawMessageAndAddsTheExplanation()
    {
        var json = DebuggerErrors.ErrorResponse(
            new COMException("The process is not synchronized.", Cor.CORDBG_E_PROCESS_NOT_SYNCHRONIZED));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.IsFalse(root.GetProperty("success").GetBoolean());
        Assert.AreEqual("The process is not synchronized.", root.GetProperty("error").GetString());
        Assert.Contains("wait_for_stop", root.GetProperty("explanation").GetString()!);
    }

    /// <summary>
    /// explanation is always present so a client can read it unconditionally, and null when the
    /// failure is not an ICorDebug one
    /// </summary>
    [TestMethod]
    public void ErrorResponse_UnknownFailure_StillCarriesANullExplanation()
    {
        var json = DebuggerErrors.ErrorResponse(new ArgumentException("Process ID must be positive, got: 0"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.IsFalse(root.GetProperty("success").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("explanation").ValueKind);
    }
}
