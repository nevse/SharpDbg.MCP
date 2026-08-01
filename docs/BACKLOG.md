# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## P1 — real functional gaps

### Expanding an evaluated member suspends the debuggee for good
`expand_variable` works, but expanding a member whose value needs function evaluation in the target
— a record's `EqualityContract`, which is a `RuntimeType` — leaves SharpDbg's variable manager
holding a disposed handle. Every subsequent `Continue` then throws
`Handle has been disposed. (0x80131C01)`, retrying never recovers, and the debuggee stays suspended.
`DetachFromProcess` is the only way out and does still work.

Reproduced on 0.1.7 and pinned by
`ExpandVariable_MemberNeedingEvaluation_PoisonsLaterContinue`.

Reported upstream as https://github.com/MattParkerDev/sharpdbg/issues/24, with a standalone
reproduction at https://github.com/nevse/sharpdbg-bug-handle-disposed-on-continue. Two separate
defects, both confirmed by decompiling 0.1.7 and by probing a live session:

1. `ManagedDebugger.ContinueWithVariableClear` calls `VariableManager.ClearAndDisposeHandleValues`,
   whose `ICorDebugHandleValue.Dispose()` (an ICorDebugSharp extension over
   `Marshal.ThrowExceptionForHR(TryDispose())`) throws on a handle that is already disposed. The
   throw escapes the `ForEach`, so `_references.Clear()` never runs and the next `Continue` walks
   the same list into the same exception - 44 stale references were still registered after the
   failure. `ClearAndTryDisposeHandleValues` is the tolerant twin and is already what the
   disconnect path uses, which is exactly why detach recovers. Switching the continue path to it,
   or clearing in a `finally`, is a one-line fix.
2. Once the references are cleared by hand, `Continue` still needs to be called twice before the
   debuggee runs again: the member evaluation leaves an unbalanced stop, and a third call then
   fails with `CORDBG_E_SUPERFLOUS_CONTINUE`. So fixing (1) alone would turn a permanent freeze
   into a stop that takes two continues to leave.

**Effort: S upstream for (1), M upstream for (2); nothing to fix here beyond the warning already
in place - `_debugger` and `_variableManager` are both private, so a local workaround would need
reflection**


### A successful function evaluation leaves the debuggee unable to resume
Any evaluation that runs code in the target - a property getter, `ToString()`, any method call -
leaves the process needing a second `Continue` before it actually resumes, and a third then fails
with `CORDBG_E_SUPERFLOUS_CONTINUE`. The first `Continue` reports success, so the process looks
running while it is suspended and nothing in the API says otherwise.

Measured on 0.1.7 by comparing four cases:

| what happened before the continue | continues needed |
|---|---|
| exception stop, no evaluation | 1, clean |
| breakpoint stop, evaluation that failed | 1, clean |
| breakpoint stop, `point.ToString()` | 2, then `SUPERFLOUS_CONTINUE` |
| exception stop, `ManagedDebugger.ExceptionInfo` (4 property reads) | never resumed |

This is the same defect as (2) above, but it is not limited to expanding variables: it reaches
`evaluate_expression`, which ships today, and it is why `get_exception_info` was not added - the
upstream `ExceptionInfo` reads Message, HResult, Source and StackTrace through property getters, so
retrieving exception details costs the session. `expand_variable` and `evaluate_expression` now warn
about it. Worth adding to https://github.com/MattParkerDev/sharpdbg/issues/24, which currently
describes the narrower variable-expansion case.

A local workaround would mean issuing extra continues after an evaluation, but the number needed is
not predictable - two was enough after one evaluation, not after four - and guessing high throws
`SUPERFLOUS_CONTINUE`, so there is nothing safe to do here beyond the warnings.
**Effort: M upstream**

### Evaluated objects cannot be expanded
`ManagedDebugger.Evaluate` hardcodes `variablesReference` to 0 on every path, so the result of
`evaluate_expression` is only ever a string — unlike `get_variables`, whose entries can be walked
with `expand_variable`. Needs a change in SharpDbg itself: the evaluated `ICorDebugValue` would
have to be registered with the variable manager the way scope members already are.
`EvaluateExpression_DoesNotYetYieldAnExpandableReference` pins the current behaviour.
**Effort: M (upstream)**

## P2 — capabilities the current package already supports

### Breakpoint calls race with module loading
`ManagedDebugger` keeps its loaded modules in a plain `Dictionary` that the module-load callback
writes to on the debugger's own thread, while binding enumerates it on the caller's thread. A
breakpoint set while the debuggee is still loading assemblies therefore fails with
`InvalidOperationException: Collection was modified; enumeration operation may not execute`, thrown
from `Dictionary.ValueCollection.Enumerator.MoveNext()` inside `SetFunctionBreakpoints`. Line
breakpoints reach the same enumeration through `TryBindBreakpoint`, so both are exposed; function
breakpoints hit it more often because they enumerate every module for every request.

`DebugSession.RetryWhileModulesLoad` retries such a call, which is safe only because both
`SetBreakpoints` and `SetFunctionBreakpoints` replace a whole set. The real fix is upstream: guard
`_modules` or hand out a snapshot. Worth reporting - it is a different defect from
https://github.com/MattParkerDev/sharpdbg/issues/24.
**Effort: S upstream, mitigated here**

### Exception stops cannot be narrowed down
`set_exception_break_mode` can only turn first-chance exception stops on or off wholesale. Two
things are missing and both need upstream work:

- **Unhandled only**, which is what a debugger normally defaults to. `HandleException` discards the
  callback's event type, so a stop carries no way to tell whether the program will handle the
  exception.
- **Filtering by exception type**, which would need the type at the moment of the stop, and reading
  it means running code in the target - see the evaluation defect above.

**Effort: M upstream, S here once the type is available**

### Surface decompiled source
`OnStopped2` carries a `DecompiledSourceInfo` that is currently discarded, so stopping in code
without PDBs reports no location at all.
**Effort: M**

## P2 — robustness and clarity

### The test host segfaults in mscordbi about one run in six
A full test run occasionally dies mid-run with `Test Run Aborted` and no managed error - the log
stops mid-line, which is a native crash. The macOS crash reports name it:

```
SIGSEGV in libmscordbi.dylib
  ShimProcess::QueueFakeAssemblyAndModuleEvent(ICorDebugAssembly*)
  ShimProcess::QueueFakeAttachEvents()
  ShimProcess::QueueFakeAttachEventsIfNeeded(bool)
  CordbRCEventThread::FlushQueuedEvents(CordbProcess*)
  CordbRCEventThread::ThreadProc()
```

That is the CLR debugger shim replaying the synthetic assembly and module load events for a fresh
attach, on its own event thread - the same replay whose timing the breakpoint bind wait works
around. Nothing of ours is on the stack.

Not caused by any recent change: reproduced at 26b4626 as well, roughly one run in six on macOS
arm64 with .NET 10.0.0, while the integration tests attach and detach around thirty times in a
single test host. A long-lived server that attaches repeatedly could hit it too, but one attach per
session makes it far less likely there.

How often it happens depends on how soon after the debuggee starts the debugger attaches. Adding the
function breakpoint tests took it to three runs in six; waiting half a second after the debuggee is
up, before attaching, brought it back to one in eight. `DebuggeeProcess.AttachSettleTime` is that
wait. Nothing in-process can do better - the shim dies on its own thread, so there is no exception to
catch.

Next steps would be to check whether it also happens on Linux and Windows, and to report it to
dotnet/runtime with a reduced repro. Until then it shows up as an occasional aborted run - the giveaway
is `Test Run Aborted` with the log cut off mid-line and no failing test - that passes on re-run.
**Effort: M to investigate**


### Multi-session support is dead code
`DebugSessionManager.CreateSession/GetSession/CloseSession` are never called; every tool goes
through `GetOrCreateCurrentSession()`. Either add `session_id` to the tool signatures and finish
it, or delete the manager and be honest about supporting one session.
**Effort: M**

### `AllowOtherUserProcesses` is never enforced
The setting is parsed and validated but nothing reads it, so the server will happily attach to any
process the user can reach. Either enforce an owner check in `ProcessDiscovery` or remove the
setting — a security option that does nothing is worse than no option.
**Effort: M**

### Process discovery is heuristic
`ProcessDiscovery.IsDotNetProcess` matches on process name (`dotnet`, `testhost`), so
self-contained apps are invisible. The diagnostic IPC channel that `dotnet-trace ps` uses is exact.
`ListDotNetProcesses` also calls `GetProcessById` again for every process it already enumerated.
**Effort: M**

### Tools are static, which blocks testing
`DebuggingTools` is a static class with `Lazy` singletons even though `Program` already builds a DI
container. Instance tools with injected dependencies would let the tool layer be tested directly
instead of only through `DebugSession`.
**Effort: M**

## P3 — distribution

### Ship on NuGet and run via `dnx`
Installation currently requires cloning, building, and pasting an absolute path into MCP config —
the README still contains a literal `/absolute/path/to/...` placeholder, which is exactly how a
"configured" server ends up silently never loading. Packaging the server on NuGet reduces install
to one copy-pasteable command with no placeholder. Optionally publish `server.json` to the official
MCP registry.
**Effort: M**
