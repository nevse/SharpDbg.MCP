# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Defects in SharpDbg and one in the .NET debugger shim limit what this server can do.

Short version, on SharpDbg 0.1.14: exception stops cannot be filtered by type or handled-ness, and the
test suite is occasionally killed by a crash inside `libmscordbi` during attach - that one is a .NET
runtime bug rather than a SharpDbg one. Stepping into code without symbols, hits on a just-replaced
breakpoint, terminating a running debuggee, and the pid and exit code of a launched program all used to
belong here; every one of them is fixed upstream and in the version we build against.

The pid and the exit code shipped in 0.1.14 and are wired through: `start_program` names the process it
created, and `get_process_status` reports how a launched program ended. An **attached** process still
has no exit code, and that is a limit of the debugger rather than something waiting on a release: it
reads the code off a process it started itself, has none for one it was merely pointed at, and the
protocol cannot say "unknown" - only `0`, which would be a number invented here. Null is the honest
answer.

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

Distribution used to be a section here. It is done: the package is on nuget.org, the registry entry is
published, and `publish-registry.yml` keeps the two in step on every release. See
[RELEASING.md](RELEASING.md).
