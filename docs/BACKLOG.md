# Backlog

Prioritized by how much each item blocks someone actually debugging with this server.
Effort is a rough estimate: S = under an hour, M = half a day, L = a day or more.

## Waiting on upstream

Nothing, as of 20 August 2026. Three gaps were found in
[clrdbg](https://github.com/JaneySprings/clrdbg), the debugger this server drives, all three were
reported from here, and all three are fixed upstream:

- A pause issued in a freshly attached debuggee's first moment used to be answered with success without
  stopping the program, and nothing downstream could tell, because over DAP a running process and a
  stopped one answer a stack trace alike. It is refused now, and `DebugSession.Pause` retries the
  refusal until the state clears — [clrdbg#1](https://github.com/JaneySprings/clrdbg/pull/1).
- A launched program's pid never reached the client, because no DAP `process` event was sent anywhere.
  It is sent for all three launch shapes now — [clrdbg#2](https://github.com/JaneySprings/clrdbg/pull/2).
  `DebugSession.Start` waits briefly for it, because the event races the response to the request that
  started the program, and `Launch_Start_NamesTheProcessItCreated` asserts the pid rather than reporting
  Inconclusive.
- `exceptionInfo` computed `HResult` and `Source` — four function evaluations in the target, which is
  the expensive part of that request — and then left them out of the response, along with
  `FormattedDescription`. All three are mapped now —
  [clrdbg#3](https://github.com/JaneySprings/clrdbg/pull/3), and
  `ExceptionStop_ReportsHResultAndSource` asserts the two this server exposes.

The submodule is pinned past all three, so nothing in the suite reports Inconclusive any more.

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

### A resume that sometimes does not resume, on Windows only
Seen twice, in two Windows integration runs of the clrdbg migration, one failure per run and a
different test each time:

- `ExceptionStop_WithBreakModeNever_LetsTheDebuggeeRunOn` - the debuggee printed nothing for two
  seconds after this session resumed an exception stop it had decided to ignore. Exactly one "Ignored
  exception" line was logged, so the resume was sent once and no further exception ever arrived.
- `TwoSessions_DebugTwoProcessesIndependently` - the debuggee printed nothing for two seconds after
  `close_session` released it.

Both are the same shape: a process that should have been let go stayed suspended. Four later runs of
the same job were clean, macOS and Linux have never shown it, and three runs on `main` against
SharpDbg were green before the move, so it arrived with clrdbg and it is intermittent rather than
certain.

**Hypothesis, not a finding.** ICorDebug's `Stop`/`Continue` pair is a nesting counter, and clrdbg's
`ContinueWithVariableClearAllowSuperfluousContinue` issues one `TryContinue` and swallows
`CORDBG_E_SUPERFLOUS_CONTINUE`. A stop count above one would then leave the process suspended with no
error returned to anyone - which matches every symptom above, and is worth nothing until measured.

**What it needs is one failure that talks.** Both tests now wait a further fifteen seconds and report
whether the debuggee ever printed again, and the exception one also reports what the session believed:
a session that knows it is stopped never sent the resume, while one that believes the program runs was
told the resume landed when it did not. That message is what should go upstream; two silent failures
are not enough to report.
**Effort: S** to report once it reproduces, unknown to fix.

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
