# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## P0 — broken or untrustworthy today

### `Test1.cs` is an empty placeholder
Delete it. It inflates the test count without asserting anything.
**Effort: S**

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


### Evaluated objects cannot be expanded
`ManagedDebugger.Evaluate` hardcodes `variablesReference` to 0 on every path, so the result of
`evaluate_expression` is only ever a string — unlike `get_variables`, whose entries can be walked
with `expand_variable`. Needs a change in SharpDbg itself: the evaluated `ICorDebugValue` would
have to be registered with the variable manager the way scope members already are.
`EvaluateExpression_DoesNotYetYieldAnExpandableReference` pins the current behaviour.
**Effort: M (upstream)**

### Callers must poll to notice a stop
After `continue_execution` the only way to learn that a breakpoint was hit is to call
`get_process_status` in a loop, which costs an LLM client a turn per poll. A blocking
`wait_for_stop(timeout_ms)` would collapse that to a single call.
**Effort: S**

### README does not match the server
Tools are documented in PascalCase (`AttachToProcess`) but the MCP SDK exposes them snake_cased
(`attach_to_process`), so anything scripted from the README fails with `Unknown tool`. The README
also does not mention that breakpoints need portable PDBs next to the target assembly, which is the
most common reason a breakpoint stays unverified.
**Effort: S**

## P2 — capabilities the current package already supports

### Function breakpoints
0.1.7 supports breakpoints bound by function rather than file/line
(`BreakpointInfo.IsFunctionBreakpoint`). Useful when the caller knows a method name but not a path.
**Effort: M**

### Break on exception
No way to stop when an exception is thrown, which is one of the main reasons to attach a debugger
at all. `ManagedDebugger` already raises `OnStopped` with reason `"exception"`.
**Effort: M**

### Surface decompiled source
`OnStopped2` carries a `DecompiledSourceInfo` that is currently discarded, so stopping in code
without PDBs reports no location at all.
**Effort: M**

## P2 — robustness and clarity

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

### COM errors leak to callers
`CORDBG_E_SUPERFLOUS_CONTINUE` is handled, but every other `CORDBG_E_*` still reaches the client as
raw COM exception text. Map the known HRESULTs to explanations.
**Effort: S**

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
