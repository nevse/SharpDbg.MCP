using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

using Newtonsoft.Json.Linq;

using SharpDbg.InMemory;
using SharpDbg.MCP.Logging;

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
    /// Thread id, reason, and the adapter ids of the breakpoints the stop is attributed to. A DAP stop
    /// carries no source location - SharpDbg does attach one as an additional property, but
    /// <c>ProtocolObject.AdditionalProperties</c> is not public, so the location is read from the top
    /// stack frame instead, by whoever needs it. It must not be read here: this runs on the protocol's
    /// reader thread, which is also what reads request responses.
    /// </summary>
    public event Action<int, string, IReadOnlyList<int>?>? OnStopped;

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
        Initialize();

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

    /// <summary>
    /// Prepares a program to be debugged and returns before it runs: the adapter only records the
    /// launch and performs it on configurationDone, which is what <see cref="Start"/> sends. Anything
    /// set in between - breakpoints above all - is already in place when the program starts, and that
    /// is the only way to debug its startup, since SharpDbg accepts stopAtEntry and ignores it.
    /// </summary>
    public async Task Launch(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        bool justMyCode,
        TimeSpan timeout)
    {
        Initialize();

        _host.SendRequestSync(new LaunchRequest
        {
            ConfigurationProperties = new Dictionary<string, JToken>
            {
                ["name"] = "SharpDbg MCP",
                ["type"] = "coreclr",
                ["request"] = "launch",
                ["program"] = program,
                ["args"] = new JArray(arguments),
                ["cwd"] = workingDirectory,
                ["env"] = JObject.FromObject(environment),
                ["console"] = "internalConsole",
                ["justMyCode"] = justMyCode
            }
        });

        await _initialized.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the program prepared by <see cref="Launch"/>. The request returns once the process has
    /// been created and attached to, so a stop can already be on its way when it does.
    /// This is the one request that creates a process, which is why it is timed: a handler that never
    /// returns would otherwise hang start_program for good. <see cref="Disconnect"/> is timed too, so
    /// that a start which expires can still tear its half-made session down.
    /// </summary>
    public void Start(TimeSpan timeout) =>
        SendRequestWithTimeout(new ConfigurationDoneRequest(), timeout, "Starting the program");

    /// <summary>
    /// Sends a request and waits for it, giving up after <paramref name="timeout"/>.
    /// <c>SendRequestSync</c> has no timeout of its own, so a stalled handler blocks its caller for
    /// the life of the process.
    /// Giving up stops us waiting; it does not stop the adapter. SharpDbg implements no cancel
    /// handler, and it serializes every request behind one lock, so a stalled handler also holds off
    /// the disconnect a teardown would send. Disposing the adapter is the only step that does not
    /// need that lock, which is why a caller on the teardown path has to be able to reach it. A
    /// caller that is not on that path may pass an infinite timeout and wait, though none does now.
    /// </summary>
    private void SendRequestWithTimeout<TArgs>(DebugRequest<TArgs> request, TimeSpan timeout, string what)
        where TArgs : class, new()
    {
        // Same guard SendRequestSync applies: the reader thread delivers events and reads responses,
        // so blocking it on a response would deadlock
        _host.VerifySynchronousOperationAllowed();

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _host.SendRequest(
            request,
            _ => completed.TrySetResult(),
            (_, ex) => completed.TrySetException(ex));

        // Waiting on the handle rather than on the task: Task.Wait throws the fault wrapped in an
        // AggregateException, which would hide the ProtocolException the line below exists to
        // rethrow with its own type and stack, the way SendRequestSync surfaces one.
        if (!((IAsyncResult)completed.Task).AsyncWaitHandle.WaitOne(timeout))
            throw new TimeoutException(
                $"{what} did not complete within {timeout.TotalSeconds:0.#}s. The debug adapter is "
                + "still working on the request.");

        completed.Task.GetAwaiter().GetResult();
    }

    private void Initialize() =>
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

    public List<(int Id, string Name)> GetThreads()
    {
        var response = _host.SendRequestSync(new ThreadsRequest());
        return response.Threads?.Select(t => (t.Id, t.Name)).ToList() ?? [];
    }

    /// <summary>
    /// The debuggee's threads, or null if the adapter did not answer within <paramref
    /// name="timeout"/>. Exists for the pause path, which needs a thread to pause and so asks for
    /// one first: an unbounded lookup there would hang before the pause was even sent, which is
    /// exactly what bounding the pause is meant to prevent.
    /// </summary>
    public List<(int Id, string Name)>? TryGetThreads(TimeSpan timeout)
    {
        _host.VerifySynchronousOperationAllowed();

        var completed = new TaskCompletionSource<ThreadsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _host.SendRequest(
            new ThreadsRequest(),
            (_, response) => completed.TrySetResult(response),
            (_, ex) => completed.TrySetException(ex));

        if (!((IAsyncResult)completed.Task).AsyncWaitHandle.WaitOne(timeout))
        {
            // Nobody is left to receive an exception from a request that fails after this point
            completed.Task.ContinueWith(
                t => McpLogger.LogWarning(
                    "Threads request failed after the caller stopped waiting: {Error}",
                    t.Exception!.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);

            return null;
        }

        var threads = completed.Task.GetAwaiter().GetResult();
        return threads.Threads?.Select(t => (t.Id, t.Name)).ToList() ?? [];
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

    /// <summary>
    /// The exception a thread is stopped on. This is the one read that runs code in the target:
    /// Message, HResult, Source and StackTrace are property getters, evaluated one after another,
    /// so it costs far more than reading frames or locals. A thread carrying no exception is a
    /// failure rather than an empty answer, because that is all the adapter reports.
    /// Several fields of the response are dropped. The short type name and the two descriptions are
    /// assembled upstream out of what is kept here anyway; the break mode is hardcoded to Always, so
    /// the one field that exists to say how the exception will be treated says it of every exception.
    /// Inner exceptions come back empty whatever was thrown, which is why they are not read either.
    /// </summary>
    public ThrownException GetException(int threadId)
    {
        var response = _host.SendRequestSync(new ExceptionInfoRequest { ThreadId = threadId });
        var details = response.Details;

        return new ThrownException(
            details?.FullTypeName ?? response.ExceptionId,
            details?.Message,
            details?.HResult,
            details?.Source,
            details?.StackTrace);
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

    /// <summary>
    /// Suspends the debuggee, returning whether the adapter confirmed it within <paramref
    /// name="timeout"/>. <paramref name="onPaused"/> runs the moment the adapter confirms, which may
    /// be after this call has already given up waiting - that is the point of it. Recording the stop
    /// from there rather than from the return is what lets the wait be bounded at all: giving up
    /// stops us waiting but does not stop the adapter, so a pause can still land afterwards, and a
    /// caller that recorded state only on return would then believe the program is running while it
    /// is suspended.
    ///
    /// <paramref name="onPaused"/> runs on the adapter's callback thread and must not throw or block.
    /// A false return means unconfirmed, not failed. A request that actually fails throws, and does
    /// so on this thread only while this call is still waiting; afterwards there is nobody to throw
    /// to, so the failure is logged instead.
    /// </summary>
    public bool TryPause(int threadId, TimeSpan timeout, Action onPaused)
    {
        // Same guard SendRequestSync applies: the reader thread delivers events and reads responses,
        // so blocking it on a response would deadlock
        _host.VerifySynchronousOperationAllowed();

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _host.SendRequest(
            new PauseRequest { ThreadId = threadId },
            _ =>
            {
                // Before signalling, so a caller still waiting never sees the pause confirmed with
                // the state not yet written
                onPaused();
                completed.TrySetResult();
            },
            (_, ex) => completed.TrySetException(ex));

        if (!((IAsyncResult)completed.Task).AsyncWaitHandle.WaitOne(timeout))
        {
            // Nobody is left to receive an exception from a request that fails after this point
            completed.Task.ContinueWith(
                t => McpLogger.LogWarning(
                    "Pause failed after the caller stopped waiting: {Error}",
                    t.Exception!.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);

            return false;
        }

        // Waiting on the handle rather than on the task: Task.Wait throws the fault wrapped in an
        // AggregateException, which would hide the ProtocolException this rethrows with its own type
        completed.Task.GetAwaiter().GetResult();
        return true;
    }

    public void StepOver(int threadId) => _host.SendRequestSync(new NextRequest { ThreadId = threadId });

    public void StepIn(int threadId) => _host.SendRequestSync(new StepInRequest { ThreadId = threadId });

    public void StepOut(int threadId) => _host.SendRequestSync(new StepOutRequest { ThreadId = threadId });

    /// <summary>
    /// Releases the debuggee, terminating it when this session started it. Since 0.1.13 the terminate
    /// synchronizes the process itself, so this is the whole teardown rather than its second half.
    /// Its only caller is that teardown, which times it because blocking forever here costs the
    /// adapter's Dispose and every later operation on the session.
    /// </summary>
    public void Disconnect(bool terminateDebuggee, TimeSpan timeout) =>
        SendRequestWithTimeout(
            new DisconnectRequest { TerminateDebuggee = terminateDebuggee }, timeout, "Disconnecting");

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
        OnStopped?.Invoke(stopped.ThreadId ?? 0, ReasonToString(stopped.Reason), stopped.HitBreakpointIds);

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
