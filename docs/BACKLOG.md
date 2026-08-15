# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Defects in SharpDbg and one in the .NET debugger shim limit what this server can do.

Short version, on SharpDbg 0.1.13: exception stops cannot be filtered by type or handled-ness, a
launched program reports no exit code, and the test suite is occasionally killed by a crash inside
`libmscordbi` during attach - the last is a .NET runtime bug rather than a SharpDbg one. Stepping into
code without symbols, hits on a just-replaced breakpoint, and terminating a running debuggee all used
to belong here; all three are fixed upstream.

A launched program's process id is fixed upstream too, as a DAP `process` event, but is **not yet
released**. When it ships, `start_program` can report the pid instead of saying the debugger never
says what it started.

The replaced-breakpoint freeze went upstream in two halves: the recovery as
[#27](https://github.com/MattParkerDev/sharpdbg/pull/27), which landed in 0.1.10, and the throw in
`HandleBreakpoint` as [#37](https://github.com/MattParkerDev/sharpdbg/pull/37), merged and released in
0.1.13. Terminating a running debuggee went up as
[#38](https://github.com/MattParkerDev/sharpdbg/issues/38) and is fixed in 0.1.13.

### `pause_execution` can wait forever on a stalled adapter
`DebugSession.Pause` passes an infinite timeout on purpose: nothing raises a stop event for a pause,
so the line after the request is the only record that the program stopped, and a timeout would skip
it while the pause still landed — leaving the session insisting the program runs while it stands
still. That is a silent lie on a common path, traded for a hang on a rare one.

The hang is narrower than it looks. When the debuggee is actually running, `StoppedThreadOrFirst`
falls through to `GetThreads`, itself an unbounded request, before the pause is even sent — so the
bound never covered the ordinary case. It covered one state: already stopped, `_lastStoppedThreadId`
retained, adapter wedged, reachable in practice only via `evaluate_expression`. Six sibling
operations hang identically in that state, so pause is not the outlier.

Worth doing only as the shape `start_program` already uses: bound the request and tear the session
down on expiry, so no reusable state survives a pause that may still land. Recording a genuinely
unknown state instead would reach `ExecutionState`, `get_process_status`, `wait_for_stop` and the
README, which is out of proportion to this window.
**Effort: M**

### Consider a `get_exception_info` tool
Now possible: reading an exception's message, type, HResult, source and stack trace costs four
function evaluations in the target, which was ruinous while an evaluation left the debuggee unable to
resume and is merely slow now. The stop still does not say the exception's type, so this is the only
way to learn it.
**Effort: S**

## P3 — distribution

### Publish to the official MCP Registry
The NuGet side is done: the package is `DotnetDebugger.Mcp`, typed `McpServer`, and carries
`.mcp/server.json`, so nuget.org lists it under `packagetype=mcpserver` and generates client
configuration from the manifest. The registry is the remaining half, and it is the upstream source
other registries read, including GitHub's.

It cannot be done until the first package is live, because the registry verifies the package exists
and that its README declares the matching `mcp-name`. First publish also establishes the
`io.github.nevse/*` namespace, so it is worth doing by hand once and watching it, rather than
automating it blind. `mcp-publisher login github`, then `mcp-publisher publish`. Automating it from
CI afterwards uses GitHub OIDC.
**Effort: S**
