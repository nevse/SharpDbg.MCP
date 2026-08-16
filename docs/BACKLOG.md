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

### Most adapter requests still have no bound
`pause_execution` used to be the named case and is now bounded: `DapDebugger.TryPause` and
`TryGetThreads` take a timeout, and the stop is recorded from the adapter's own confirmation rather
than after the wait, so giving up waiting no longer risks the session claiming a program runs while
it stands still.

Its siblings were never the outlier and are still unbounded. `get_threads`, `get_stack_trace`,
`get_variables`, `expand_variable`, `evaluate_expression`, `get_exception_info`, the three steps and
`continue_execution` all go through `SendRequestSync`, which has no timeout, so a wedged adapter
blocks the caller for the life of the process. SharpDbg serializes every request behind one lock, so
the first one to wedge holds off the rest, including a disconnect.

`evaluate_expression` and `get_exception_info` look bounded and are not: both wrap the request in
`Task.Run(...).WaitAsync(_evaluationTimeout)`, which releases the caller and leaves the pool thread,
the adapter and its lock exactly where they were. Worth knowing before either is counted as done.

The pause fix is the shape to copy: bound the request, and write whatever state the operation records
from the completion callback rather than after the wait. Doing it wholesale means a decision about
what each tool reports when unconfirmed, which is why it is not a mechanical change.
**Effort: M**

## P3 — distribution

### Keep the MCP Registry entry in step with releases
Both halves of the first publish are done. The package is `DotnetDebugger.Mcp`, typed `McpServer` and
carrying `.mcp/server.json`, so nuget.org lists it under `packagetype=mcpserver`; and the registry
entry `io.github.nevse/dotnet-debugger-mcp` was published by hand on 15 August 2026, which claimed the
`io.github.nevse/*` namespace. Check it with:

```bash
curl -s "https://registry.modelcontextprotocol.io/v0/servers?search=io.github.nevse"
```

What is left is that nothing re-publishes it. The registry still reports 0.1.0 while nuget.org has
0.1.1, and it will fall a release further behind each time. The `package` job already holds the GitHub
OIDC token `mcp-publisher login github` wants and already knows the version, so the step belongs there
rather than in a checklist someone has to remember.
**Effort: S**
