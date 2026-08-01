# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Five defects in SharpDbg and one in the .NET debugger shim limit what this server can do. They are
tracked in [UPSTREAM.md](UPSTREAM.md) with the evidence, what each one blocks here, and how to tell
when a fix lands. Nothing in that file is work we can do, which is why it is not listed below.

Short version: an evaluation that runs code in the target can leave the debuggee suspended for good;
evaluation results cannot be expanded; exception stops cannot be filtered by type or handled-ness; and
the test suite is occasionally killed by a crash inside `libmscordbi` during attach.

## P2 — capabilities and robustness

### Surface decompiled source
`OnStopped2` carries a `DecompiledSourceInfo` that is currently discarded, so stopping in code
without PDBs reports no location at all.
**Effort: M**

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
