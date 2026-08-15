# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Defects in SharpDbg and one in the .NET debugger shim limit what this server can do.

Short version, on SharpDbg 0.1.12: exception stops cannot be filtered by type or handled-ness, a
launched program reports neither its process id nor its exit code, and the test suite is occasionally
killed by a crash inside `libmscordbi` during attach - the last is a .NET runtime bug rather than a
SharpDbg one. Stepping into code without symbols and hits on a just-replaced breakpoint used to belong
here; both are fixed upstream.

Terminating a *running* debuggee fails inside ICorDebug and reports success anyway, which would leak
every program we launch. Closing a session pauses first, which makes the terminate land, so this one
costs us a workaround rather than a limitation.

The recovery half of the replaced-breakpoint freeze went upstream as
[#27](https://github.com/MattParkerDev/sharpdbg/pull/27) and landed in 0.1.10; the throw in
`HandleBreakpoint` itself as [#37](https://github.com/MattParkerDev/sharpdbg/pull/37).

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

### Ship on NuGet and run via `dnx`
Installation currently requires cloning, building, and pasting an absolute path into MCP config —
the README still contains a literal `/absolute/path/to/...` placeholder, which is exactly how a
"configured" server ends up silently never loading. Packaging the server on NuGet reduces install
to one copy-pasteable command with no placeholder. Optionally publish `server.json` to the official
MCP registry.
**Effort: M**
