# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Defects in SharpDbg and one in the .NET debugger shim limit what this server can do. They are tracked
in [UPSTREAM.md](UPSTREAM.md) with the evidence, what each one blocks here, and how to tell when a fix
lands.

Short version, on SharpDbg 0.1.9 (0.1.10 is skipped, it regresses evaluation - see UPSTREAM.md defect 9): a breakpoint hit that lands while that file's breakpoints are being
replaced can leave the debuggee suspended for good, and so can a step that reaches code without
symbols; exception stops cannot be filtered by type or handled-ness; and the test suite is
occasionally killed by a crash inside `libmscordbi` during attach.

The recovery half of the replaced-breakpoint freeze went upstream as
[#27](https://github.com/MattParkerDev/sharpdbg/pull/27) and landed in 0.1.10. The other two — the
throw in `HandleBreakpoint` itself and the modules-dictionary race — are **fixed on a local branch of a
SharpDbg clone**, `fix/continue-after-failed-event-handler-on-main`, and not sent yet.

The 0.1.10 regression is [#29](https://github.com/MattParkerDev/sharpdbg/issues/29).

### Consider a `get_exception_info` tool
Now possible: reading an exception's message, type, HResult, source and stack trace costs four
function evaluations in the target, which was ruinous while UPSTREAM.md defect 2 was open and is
merely slow on 0.1.9. The stop still does not say the exception's type, so this is the only way to
learn it.
**Effort: S**

## P3 — distribution

### Ship on NuGet and run via `dnx`
Installation currently requires cloning, building, and pasting an absolute path into MCP config —
the README still contains a literal `/absolute/path/to/...` placeholder, which is exactly how a
"configured" server ends up silently never loading. Packaging the server on NuGet reduces install
to one copy-pasteable command with no placeholder. Optionally publish `server.json` to the official
MCP registry.
**Effort: M**
