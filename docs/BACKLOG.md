# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Defects in SharpDbg and one in the .NET debugger shim limit what this server can do.

Short version, on SharpDbg 0.1.9 (0.1.10 is skipped, it regresses evaluation): a breakpoint hit that lands while that file's breakpoints are being
replaced can leave the debuggee suspended for good, and so can a step that reaches code without
symbols; exception stops cannot be filtered by type or handled-ness; and the test suite is
occasionally killed by a crash inside `libmscordbi` during attach.

The recovery half of the replaced-breakpoint freeze went upstream as
[#27](https://github.com/MattParkerDev/sharpdbg/pull/27) and landed in 0.1.10; the throw in
`HandleBreakpoint` itself as [#37](https://github.com/MattParkerDev/sharpdbg/pull/37).

### Consider a `get_exception_info` tool
Now possible: reading an exception's message, type, HResult, source and stack trace costs four
function evaluations in the target, which was ruinous while an evaluation left the debuggee unable to
resume and is merely slow on 0.1.9. The stop still does not say the exception's type, so this is the only way to
learn it.
**Effort: S**

## P1 — drive SharpDbg through its supported surface

`DebugSession` calls `ManagedDebugger` directly. That type is public but is **not** the supported API
of the package: the readme documents `SharpDbgInMemory.NewDebugAdapterStreams()` plus a
`DebugProtocolHost`, and every piece of synchronisation SharpDbg has lives in `DebugAdapter`, which
that path goes through and ours does not. Confirmed by the maintainer in
[#29](https://github.com/MattParkerDev/sharpdbg/issues/29).

It already costs us. On 0.1.10 the same ten expansions return the internal error 10 times out of 10
through `ManagedDebugger` and 0 times out of 10 through the DAP host, which is what blocks the bump.
It has also broken us twice on internal signature changes - `Evaluate`'s return type, and the
`AsyncStepper` constructor - neither of which a supported consumer would have noticed.

Everything we call maps one-to-one onto requests `DebugAdapter` already handles:

| `DebugSession` uses | DAP request |
|---|---|
| `Attach`, `ConfigurationDone` | `Attach` (`processId` and `justMyCode` as configuration properties), `ConfigurationDone` |
| `SetBreakpoints`, `SetFunctionBreakpoints` | `SetBreakpoints`, `SetFunctionBreakpoints` |
| `GetThreads`, `GetStackTrace`, `GetScopes`, `GetVariables` | `Threads`, `StackTrace`, `Scopes`, `Variables` |
| `Evaluate` | `Evaluate` |
| `HandleContinueRequest`, `Pause`, `StepNext`/`StepIn`/`StepOut` | `Continue`, `Pause`, `Next`/`StepIn`/`StepOut` |
| `Disconnect` | `Disconnect` |
| `OnStopped`/`OnStopped2`, `OnExited`, `OnOutput`, `OnBreakpointChanged`, `OnContinued` | `Stopped`, `Exited`/`Terminated`, `Output`, `Breakpoint`, `Continued` events |

One real gap: `OnStopped2` hands us the file, line and column with the stop, while a DAP `Stopped`
event carries only the thread and the reason - the location needs a `StackTrace` request after it.
That changes how `DebugSession` records a stop, not what it can report. `SetExceptionBreakpoints` is
also handled upstream and may be able to replace our own ignore-and-resume implementation of
`ExceptionBreakMode`.

No new packages needed: `SharpDbg.InMemory.dll`, `SharpDbg.Application.dll` and
`Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll` are already in our build output. The
integration tests target `DebugSession` rather than the debugger, so they carry over as the safety net
for the move.
**Effort: L**

## P3 — distribution

### Ship on NuGet and run via `dnx`
Installation currently requires cloning, building, and pasting an absolute path into MCP config —
the README still contains a literal `/absolute/path/to/...` placeholder, which is exactly how a
"configured" server ends up silently never loading. Packaging the server on NuGet reduces install
to one copy-pasteable command with no placeholder. Optionally publish `server.json` to the official
MCP registry.
**Effort: M**
