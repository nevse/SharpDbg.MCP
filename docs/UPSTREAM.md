# Upstream defects we are waiting on

Defects outside this repository that limit what this server can do. None of them are work we can
finish ourselves, so they are kept out of the backlog; the point of this file is that the evidence is
written down once and a fix can be verified without re-deriving it.

Only #24 has been reported so far. The rest are deliberately held back until it is fixed, so we can
see how the maintainer wants these handled before filing more; the drafts at the bottom are ready to
send.

## Checking whether a fix has landed

SharpDbg is a package, so a fix arrives as a version bump:

```bash
dotnet list package --outdated          # is there a newer SharpDbg than 0.1.7?
```

Bump `SharpDbg` in `Directory.Packages.props`, then run the full suite. Two tests exist purely to
tell us when a limitation is gone — **they are supposed to start failing:**

| test | defect | what it failing means |
|---|---|---|
| `ExpandVariable_MemberNeedingEvaluation_PoisonsLaterContinue` | 1, 2 | the debuggee is no longer stuck after an evaluation; drop the warnings in `expand_variable`, `evaluate_expression` and `continue_execution` |
| `EvaluateExpression_DoesNotYetYieldAnExpandableReference` | 3 | evaluation results can be expanded; say so in `expand_variable`'s description |

Two defects have no test watching them:

- **4** is hidden by `DebugSession.RetryWhileModulesLoad`. To check it, look at whether
  `ManagedDebugger._modules` is still an unguarded `Dictionary`; if it is guarded, delete the retry.
- **6** is a native crash, so it can only be observed as an occasional aborted test run.

## SharpDbg (https://github.com/MattParkerDev/sharpdbg, MIT)

Everything below was confirmed on 0.1.7 both by decompiling the package and by probing a live
session.

### 1. Expanding an evaluated member leaves a disposed handle — reported as [#24](https://github.com/MattParkerDev/sharpdbg/issues/24)

Standalone reproduction: https://github.com/nevse/sharpdbg-bug-handle-disposed-on-continue
(`dotnet run --project src/Repro`, exit 0 means reproduced).

Expanding a member whose value needs function evaluation — a record's `EqualityContract`, which is a
`RuntimeType` — leaves the variable manager holding a released handle. Every later `Continue` throws
`Handle has been disposed. (0x80131C01)`, retrying never recovers, and only `Disconnect` releases the
debuggee.

`ManagedDebugger.ContinueWithVariableClear` calls `VariableManager.ClearAndDisposeHandleValues`,
whose `ICorDebugHandleValue.Dispose()` — an ICorDebugSharp extension over
`Marshal.ThrowExceptionForHR(TryDispose())` — throws on an already-released handle. The throw escapes
the `ForEach`, so `_references.Clear()` never runs: 44 references were still registered after the
failure, which is why the next `Continue` fails the same way. `ClearAndTryDisposeHandleValues` is the
tolerant twin and is already what the `Disconnect` path uses, which is exactly why detaching
recovers. Using it in the continue path, or clearing in a `finally`, is a one-line fix.

**Blocks:** nothing outright, but `expand_variable` can cost the session. The tool description warns
about it and `continue_execution` explains the HRESULT.

### 2. Any successful function evaluation leaves the debuggee unable to resume — not reported

The same area as #24 but wider: it is not about variable expansion. Any evaluation that runs code in
the target leaves the process needing a second `Continue`, and a third then fails with
`CORDBG_E_SUPERFLOUS_CONTINUE`. The first `Continue` reports success, so the process looks running
while it is suspended.

| what happened before the continue | continues needed to resume |
|---|---|
| exception stop, no evaluation | 1, clean |
| breakpoint stop, an evaluation that failed | 1, clean |
| breakpoint stop, `point.ToString()` | 2, then `SUPERFLOUS_CONTINUE` |
| exception stop, `ManagedDebugger.ExceptionInfo` (4 property reads) | never resumed |

**Blocks:** `evaluate_expression` is affected today, and this is why there is no
`get_exception_info` — the upstream `ExceptionInfo` reads Message, HResult, Source and StackTrace
through property getters, so retrieving exception details costs the session. A local workaround would
mean guessing how many extra continues to issue, and guessing high throws `SUPERFLOUS_CONTINUE`, so
there is nothing safe to do here beyond the warnings.

### 3. Evaluation results carry no variables reference — not reported

`ManagedDebugger.Evaluate` hardcodes `variablesReference` to 0 on every path, so the result of
`evaluate_expression` is only ever a string, while entries from `get_variables` can be walked with
`expand_variable`. The fix is to register the evaluated `ICorDebugValue` with the variable manager
the way scope members already are.

**Blocks:** `expand_variable` only works on variables, not on evaluation results.

### 4. The modules dictionary is not thread safe — not reported

`_modules` is a plain `Dictionary` written by the module-load callback on the debugger's own thread
and enumerated on the caller's thread while breakpoints bind. A breakpoint set while the debuggee is
still loading assemblies fails with:

```
System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
   at System.Collections.Generic.Dictionary`2.ValueCollection.Enumerator.MoveNext()
   at SharpDbg.Infrastructure.Debugger.ManagedDebugger.SetFunctionBreakpoints(...)
```

Line breakpoints reach the same enumeration through `TryBindBreakpoint`, so both kinds are exposed;
function breakpoints hit it more often because they enumerate every module for every request.

**Blocks:** nothing now — `DebugSession.RetryWhileModulesLoad` retries these calls, which is safe
only because both `SetBreakpoints` and `SetFunctionBreakpoints` replace a whole set.

### 5. An exception stop says neither the type nor whether it will be handled — not reported

`HandleException` discards the callback's event type, so a stop cannot be classified as first-chance
or unhandled, and the exception's type can only be read by running code in the target, which hits
defect 2.

**Blocks:** `set_exception_break_mode` can only be all or nothing. No unhandled-only mode, which is
what a debugger normally defaults to, and no filtering by exception type.

## .NET runtime

### 6. libmscordbi segfaults while replaying attach events

A test run occasionally dies with `Test Run Aborted`, no failing test, and the log cut off mid-line.
The macOS crash reports put it in the debugger shim, on its own event thread:

```
SIGSEGV in libmscordbi.dylib
  ShimProcess::QueueFakeAssemblyAndModuleEvent(ICorDebugAssembly*)
  ShimProcess::QueueFakeAttachEvents()
  ShimProcess::QueueFakeAttachEventsIfNeeded(bool)
  CordbRCEventThread::FlushQueuedEvents(CordbProcess*)
  CordbRCEventThread::ThreadProc()
```

That is the synthetic assembly and module load events being queued for a fresh attach. Nothing of
ours is on the stack, and it reproduces at 26b4626, before any of the work that first surfaced it.

Roughly one full run in six to eight on macOS arm64 with .NET 10.0.0, where the integration tests
attach and detach around thirty times in one test host. The rate depends on how soon after the
debuggee starts the debugger attaches: adding six more attaches took it to three runs in six, and
waiting half a second before attaching (`DebuggeeProcess.AttachSettleTime`) brought it back to one in
eight. Nothing in-process can do better, since the shim dies on a thread we do not own.

A server that attaches once per session is far less exposed than the test suite.

**Before reporting to dotnet/runtime:** check whether it also happens on Linux and Windows, and
reduce it to a repro that only attaches and detaches in a loop.

## Drafts, ready to send

### Comment on #24, for defect 2

> The disposed handle is one half of this. The other is that **any** successful function evaluation
> leaves the process needing a second `Continue` before it resumes, whether or not a handle was
> disposed - and the first `Continue` reports success, so the process looks running while it is
> suspended.
>
> Measured on 0.1.7, comparing four cases: [table from defect 2]
>
> So fixing the disposal alone turns a permanent freeze into a stop that takes two continues to
> leave. `ExceptionInfo` is worth checking as well, since its four property reads leave the process
> unable to resume at all.

### New issue, for defect 4

> **Title:** Breakpoint calls race with module loading: `_modules` is enumerated while the load
> callback writes to it
>
> A breakpoint set while the debuggee is still loading assemblies fails with
> `InvalidOperationException: Collection was modified` from inside `SetFunctionBreakpoints`
> [stack from defect 4]. `_modules` is a plain `Dictionary`; `HandleModuleLoaded` writes to it on the
> runtime callback thread while `SetFunctionBreakpoints` and `TryBindBreakpoint` enumerate it on the
> caller's. Guarding it, or binding against a snapshot, would fix both paths.
