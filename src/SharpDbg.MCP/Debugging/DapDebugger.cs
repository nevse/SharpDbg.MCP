using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

using Newtonsoft.Json.Linq;

using SharpDbg.InMemory;

using MSBreakpoint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Breakpoint;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Drives SharpDbg through the surface its package actually supports: an in-process debug adapter
/// spoken to over DAP. The alternative - calling <c>ManagedDebugger</c> directly - is public but
/// unsupported, and misses every piece of synchronisation SharpDbg has, all of which lives in its
/// DebugAdapter.
/// Requests are never sent from an event handler: events are delivered on the protocol's reader
/// thread, which is also what would have to read the response.
/// </summary>
internal sealed class DapDebugger : IDisposable
{
    private readonly DebugProtocolHost _host;
    private readonly IDisposable _adapter;
    private readonly Action<string>? _logger;
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _disposed;

    /// <summary>
    /// Thread id and reason. A DAP stop carries no source location - SharpDbg does attach one as an
    /// additional property, but <c>ProtocolObject.AdditionalProperties</c> is not public, so the
    /// location is read from the top stack frame instead, by whoever needs it. It must not be read
    /// here: this runs on the protocol's reader thread, which is also what reads request responses.
    /// </summary>
    public event Action<int, string>? OnStopped;

    public event Action<int>? OnContinued;
    public event Action? OnExited;
    public event Action<string, bool>? OnOutput;
    public event Action<AppliedBreakpoint>? OnBreakpointChanged;

    public DapDebugger(Action<string>? logger = null)
    {
        _logger = logger;

        var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams(logger);
        _adapter = adapter;
        _host = new DebugProtocolHost(input, output, false);

        _host.RegisterEventType<InitializedEvent>(_ => _initialized.TrySetResult());
        _host.RegisterEventType<StoppedEvent>(OnStoppedEvent);
        _host.RegisterEventType<ContinuedEvent>(e => OnContinued?.Invoke(e.ThreadId));
        _host.RegisterEventType<ExitedEvent>(_ => OnExited?.Invoke());
        _host.RegisterEventType<TerminatedEvent>(_ => OnExited?.Invoke());
        _host.RegisterEventType<OutputEvent>(OnOutputEvent);
        _host.RegisterEventType<BreakpointEvent>(OnBreakpointEvent);

        _host.VerifySynchronousOperationAllowed();
        _host.Run();
    }

    /// <summary>
    /// Initializes the adapter, attaches, and waits for the attach to land. The DAP order is
    /// initialize, attach, wait for initialized, configurationDone - breakpoints would normally be
    /// sent between the last two, which an MCP server cannot do because they arrive as separate tool
    /// calls long afterwards.
    /// </summary>
    public async Task Attach(int processId, bool justMyCode, TimeSpan timeout)
    {
        _host.SendRequestSync(new InitializeRequest
        {
            ClientID = "sharpdbg-mcp",
            ClientName = "SharpDbg MCP Server",
            AdapterID = "coreclr",
            Locale = "en-us",
            LinesStartAt1 = true,
            ColumnsStartAt1 = true,
            PathFormat = InitializeArguments.PathFormatValue.Path,
            SupportsVariableType = true
        });

        _host.SendRequestSync(new AttachRequest
        {
            ConfigurationProperties = new Dictionary<string, JToken>
            {
                ["name"] = "SharpDbg MCP",
                ["type"] = "coreclr",
                ["processId"] = processId,
                ["console"] = "internalConsole",
                ["justMyCode"] = justMyCode
            }
        });

        await _initialized.Task.WaitAsync(timeout).ConfigureAwait(false);

        _host.SendRequestSync(new ConfigurationDoneRequest());
    }

    public List<(int Id, string Name)> GetThreads()
    {
        var response = _host.SendRequestSync(new ThreadsRequest());
        return response.Threads?.Select(t => (t.Id, t.Name)).ToList() ?? [];
    }

    public List<StackFrameInfo> GetStackTrace(int threadId)
    {
        var response = _host.SendRequestSync(new StackTraceRequest { ThreadId = threadId });

        return response.StackFrames?.Select(f => new StackFrameInfo(
            f.Id,
            f.Name,
            f.Line,
            f.EndLine ?? f.Line,
            f.Column,
            f.EndColumn ?? f.Column,
            f.Source?.Path)).ToList() ?? [];
    }

    /// <summary>
    /// The variables of a frame. SharpDbg exposes a single scope per frame, which already covers the
    /// current exception, the arguments and the locals.
    /// </summary>
    public List<VariableInfo> GetFrameVariables(int frameId)
    {
        var scopes = _host.SendRequestSync(new ScopesRequest { FrameId = frameId });
        var first = scopes.Scopes?.FirstOrDefault();

        return first is null ? [] : GetVariables(first.VariablesReference);
    }

    public List<VariableInfo> GetVariables(int variablesReference)
    {
        var response = _host.SendRequestSync(new VariablesRequest { VariablesReference = variablesReference });

        return response.Variables?
            .Select(v => new VariableInfo(v.Name, v.Value, v.Type, v.VariablesReference))
            .ToList() ?? [];
    }

    public EvaluationResult Evaluate(string expression, int frameId)
    {
        var response = _host.SendRequestSync(new EvaluateRequest { Expression = expression, FrameId = frameId });
        return new EvaluationResult(response.Result, response.Type, response.VariablesReference);
    }

    public List<AppliedBreakpoint> SetBreakpoints(string filePath, IReadOnlyList<SourceBreakpointRequest> breakpoints)
    {
        var response = _host.SendRequestSync(new SetBreakpointsRequest
        {
            Source = new Source { Path = filePath },
            Breakpoints = breakpoints
                .Select(b => new SourceBreakpoint
                {
                    Line = b.Line,
                    Condition = b.Condition,
                    HitCondition = b.HitCondition
                })
                .ToList()
        });

        return Map(response.Breakpoints);
    }

    public List<AppliedBreakpoint> SetFunctionBreakpoints(IReadOnlyList<FunctionBreakpointRequest> breakpoints)
    {
        var response = _host.SendRequestSync(new SetFunctionBreakpointsRequest
        {
            Breakpoints = breakpoints
                .Select(b => new FunctionBreakpoint(b.FunctionName)
                {
                    Condition = b.Condition,
                    HitCondition = b.HitCondition
                })
                .ToList()
        });

        return Map(response.Breakpoints);
    }

    public void Continue(int threadId) => _host.SendRequestSync(new ContinueRequest { ThreadId = threadId });

    public void Pause(int threadId) => _host.SendRequestSync(new PauseRequest { ThreadId = threadId });

    public void StepOver(int threadId) => _host.SendRequestSync(new NextRequest { ThreadId = threadId });

    public void StepIn(int threadId) => _host.SendRequestSync(new StepInRequest { ThreadId = threadId });

    public void StepOut(int threadId) => _host.SendRequestSync(new StepOutRequest { ThreadId = threadId });

    public void Disconnect(bool terminateDebuggee) =>
        _host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = terminateDebuggee });

    private static List<AppliedBreakpoint> Map(List<MSBreakpoint>? breakpoints)
    {
        return breakpoints?
            .Select(b => new AppliedBreakpoint(
                b.Id ?? 0,
                b.Verified,
                b.Message,
                b.Line,
                b.Source?.Path))
            .ToList() ?? [];
    }

    private void OnStoppedEvent(StoppedEvent stopped) =>
        OnStopped?.Invoke(stopped.ThreadId ?? 0, ReasonToString(stopped.Reason));

    private void OnOutputEvent(OutputEvent output)
    {
        if (output.Output is null)
            return;

        OnOutput?.Invoke(output.Output, output.Category == OutputEvent.CategoryValue.Stderr);
    }

    private void OnBreakpointEvent(BreakpointEvent breakpointEvent)
    {
        var breakpoint = breakpointEvent.Breakpoint;

        if (breakpoint is null)
            return;

        OnBreakpointChanged?.Invoke(new AppliedBreakpoint(
            breakpoint.Id ?? 0,
            breakpoint.Verified,
            breakpoint.Message,
            breakpoint.Line,
            breakpoint.Source?.Path));
    }

    /// <summary>
    /// Back to the debugger's own vocabulary, which is what the session and its callers speak
    /// </summary>
    private static string ReasonToString(StoppedEvent.ReasonValue reason) => reason switch
    {
        StoppedEvent.ReasonValue.Step => "step",
        StoppedEvent.ReasonValue.Breakpoint => "breakpoint",
        StoppedEvent.ReasonValue.FunctionBreakpoint => "breakpoint",
        StoppedEvent.ReasonValue.Exception => "exception",
        StoppedEvent.ReasonValue.Pause => "pause",
        StoppedEvent.ReasonValue.Entry => "entry",
        StoppedEvent.ReasonValue.Goto => "goto",
        _ => "unknown"
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _adapter.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Failed to release the debug adapter: {ex.Message}");
        }
    }
}

/// <summary>A breakpoint as the adapter reports it back</summary>
internal sealed record AppliedBreakpoint(int Id, bool Verified, string? Message, int? Line, string? SourcePath);

/// <summary>A source breakpoint to apply</summary>
internal sealed record SourceBreakpointRequest(int Line, string? Condition, string? HitCondition);

/// <summary>A function breakpoint to apply</summary>
internal sealed record FunctionBreakpointRequest(string FunctionName, string? Condition, string? HitCondition);
