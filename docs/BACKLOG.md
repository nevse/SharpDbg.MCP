# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Two gaps in [clrdbg](https://github.com/JaneySprings/clrdbg), the debugger this server drives, limit
what it can report. Each is pinned by a test that reports Inconclusive rather than passing, so both
start failing the day they are fixed:

- **No DAP `process` event.** The pid of a program the debugger launched never reaches the client, so
  `start_program` reports `process_id: null`. A process this server attached to needs no event, because
  the caller named it. `Launch_Start_NamesTheProcessItCreated` holds the place.
- **`exceptionInfo` drops `HResult` and `Source`.** The engine reads both out of the target — four
  function evaluations, which is the expensive part of that request — and then leaves them out of the
  response, so the cost is paid and the values are lost. `get_exception_info` answers null for both.
  `ExceptionStop_HResultAndSource_AreDroppedByTheAdapter` holds that one.

Both were reported alongside a third, which is fixed: a pause issued in a freshly attached debuggee's
first moment used to be answered with success without stopping the program, and nothing downstream
could tell, because over DAP a running process and a stopped one answer a stack trace alike. It is
refused now, and `DebugSession.Pause` retries the refusal until the state clears —
[clrdbg#1](https://github.com/JaneySprings/clrdbg/pull/1), merged, with the submodule pinned past it.

An **attached** process still has no exit code, and that one is waiting on nobody: the debugger reads
the code off a process it started itself and has none for one it was merely pointed at, and the
protocol cannot say "unknown" — only `0`, which would be a number invented here. Null is the honest
answer. A program started with `launch_program` reports the code it really returned.

The crash inside `libmscordbi` during attach, a .NET runtime bug rather than a debugger one, used to
kill whole test runs. It cannot any more: the adapter runs in a process of its own, so a crash there
fails the session that caused it and leaves the server standing, which
`AdapterKilledOutright_FailsTheOperationAndLeavesUsRunning` pins.

Everything else that stood in this section was a SharpDbg defect and left with the dependency —
exception stops that could not be filtered by type or handled-ness, which `set_exception_break_mode`
now does; stepping into code without symbols; a hit on a just-replaced breakpoint; terminating a
running debuggee. Nothing here re-tests the last three against clrdbg, so if it shares any of them,
they will arrive as new findings rather than as confirmations.

### Most adapter requests still have no bound
`pause_execution` used to be the named case and is now bounded: `DapDebugger.TryPause` and
`TryGetThreads` take a timeout, and the stop is recorded from the adapter's own confirmation rather
than after the wait, so giving up waiting no longer risks the session claiming a program runs while
it stands still.

Its siblings were never the outlier and are still unbounded. `get_threads`, `get_stack_trace`,
`get_variables`, `expand_variable`, `evaluate_expression`, `get_exception_info`, the three steps and
`continue_execution` all go through `SendRequestSync`, which has no timeout, so a wedged adapter
blocks the caller for the life of the process.

The adapter cannot spread the damage over other requests either, because it answers them one at a
time: measured against clrdbg, a `threads` request that takes 9ms took 1861ms while a pause ahead of
it was waiting, and a `disconnect` would have waited exactly as long. So the first request to wedge
holds off every other one, including the one that would tear the session down.

`evaluate_expression` and `get_exception_info` look bounded and are not: both wrap the request in
`Task.Run(...).WaitAsync(_evaluationTimeout)`, which releases the caller and leaves the pool thread,
the adapter and its lock exactly where they were. Worth knowing before either is counted as done.

The pause is the shape to copy: bound the request, and write whatever state the operation records
from the completion callback rather than after the wait. Doing it wholesale means a decision about
what each tool reports when unconfirmed, which is why it is not a mechanical change.
**Effort: M**

Distribution used to be a section here. It is done: the package is on nuget.org with the debug adapter
inside it, the registry entry is published, and `publish-registry.yml` keeps the two in step on every
release. See [RELEASING.md](RELEASING.md).
