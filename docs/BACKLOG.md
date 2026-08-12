# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Defects in SharpDbg and one in the .NET debugger shim limit what this server can do.

Short version, on SharpDbg 0.1.12: exception stops cannot be filtered by type or handled-ness, and
the test suite is occasionally killed by a crash inside `libmscordbi` during attach - the second is a
.NET runtime bug rather than a SharpDbg one. Stepping into code without symbols and hits on a
just-replaced breakpoint used to belong here; both are fixed upstream.

The recovery half of the replaced-breakpoint freeze went upstream as
[#27](https://github.com/MattParkerDev/sharpdbg/pull/27) and landed in 0.1.10; the throw in
`HandleBreakpoint` itself as [#37](https://github.com/MattParkerDev/sharpdbg/pull/37).

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
