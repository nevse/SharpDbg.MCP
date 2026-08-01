# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## P0 — broken or untrustworthy today

### The Code Quality job cannot fail
Both of its steps carry `continue-on-error: true`, so the job reports success no matter what it
finds. It is currently hiding two real failures on every run:

- `dotnet format --verify-no-changes` exits with code 2 on 24 violations across 16 files
- `dotnet build /p:TreatWarningsAsErrors=true` produces 11 `MSTEST0037` errors in
  `ServerConfigurationTests.cs` (`Assert.AreEqual` where `Assert.IsTrue`/`IsFalse`/`Contains` is
  meant)

Both surface in the run summary as failure annotations while the checkmark stays green, which is
the worst of both worlds: loud enough to be noticed, powerless to block anything.

Fix the two underlying problems, then remove `continue-on-error`. Most of the format violations
trace to one line: `.editorconfig` sets `insert_final_newline = false` while every file in the
repository ends with a newline, so the setting contradicts the code it governs.

There is also no `global.json`, so the SDK is whatever each developer has installed.
Pin it while fixing this.
**Effort: S**

### Integration tests do not run in CI
`.github/workflows/ci.yml` filters out `TestCategory=Integration` because attaching a real
ICorDebug debugger to a child process is not reliably permitted on hosted runners. Until this is
resolved the regression net only exists on developer machines. Investigate whether the Ubuntu
runner allows the attach (ptrace scope, container capabilities); if it does, add a dedicated job.
**Effort: M**

### `Test1.cs` is an empty placeholder
Delete it. It inflates the test count without asserting anything.
**Effort: S**

## P1 — real functional gaps

### No way to remove or list breakpoints
`set_breakpoint` exists; there is no `remove_breakpoint` and no `list_breakpoints`. Once a
breakpoint is set the only way to get rid of it is to detach. `BreakpointManager` already supports
clearing, and `DebugSession` already tracks every breakpoint it has set.
**Effort: S**

### Objects cannot be expanded
`get_variables` returns a `variables_reference` for every non-primitive value, but there is no tool
that takes one and returns the nested members — so any object is visible only as its string form.
`DebugSession.GetVariables` also reads `scopes[0]` only and silently drops the remaining scopes.
**Effort: M**

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

### Conditional and hit-count breakpoints
SharpDbg 0.1.7 takes `SharpDbgBreakpointRequest(Line, Condition, HitCondition, Column)` and
`DebugSession.SetBreakpoint` already accepts a condition — it is simply not exposed as an MCP tool
parameter. Nearly free.
**Effort: S**

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
