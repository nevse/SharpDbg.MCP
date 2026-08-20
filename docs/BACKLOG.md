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
running debuggee. The last three were re-tested against clrdbg on 20 August 2026 and none of them
reproduces through this server; `UpstreamProbeTests` is what settled that and stays as the guard.

The terminate one is the only one with anything left in it. clrdbg's `Terminate` has the shape the
defect describes — no stop before the terminate, and a failure that is only logged — and a running
**attached** process does survive it, measured in clrdbg's own fixture. It never reaches us: clrdbg's
`Dispose` kills the OS process it started itself, and this server only ever asks to terminate a
program it launched. The two terminate probes pin the first of those — a launched program does not
outlive its session, running or stopped; the attached side is pinned by
`TwoSessions_DebugTwoProcessesIndependently`. Filed upstream as
[clrdbg#4](https://github.com/JaneySprings/clrdbg/pull/4).

### A resume that sometimes does not resume, on Windows only — closed, it did resume

**Closed on 20 August 2026.** The failure reproduced once more, on the Windows integration leg of
[#10](https://github.com/nevse/dotnet-debugger-mcp/pull/10), and the diagnostics armed for it answered
the question in one line:

```
Debuggee stayed suspended on an exception that should have been resumed.
The session says running=True, reason=none, seen=1, ignored=1;
the debuggee printed again after a further 0.9s
```

The program was not stranded. It resumed, and printed again about three seconds after the exception
stop rather than within the two the test allowed. The session knew it had resumed throughout —
`running=True`, one exception seen and one ignored — so nothing was lost anywhere.

The nesting-counter hypothesis recorded here was wrong, and so was the name of the entry. What the two
failures actually shared was the **test** shape, not a debugger defect: both asserted that a program is
running by counting lines printed inside a fixed two-second window, which measures the rate as well as
the fact. In `never` mode every throw costs a round trip to resume it, and the debuggee throws every
150ms, so a loaded Windows runner can legitimately hold it below one line per two seconds while it
settles.

Nine assertions across the suite had that shape. They now wait up to `ResumeTimeout` for the debuggee
to print at all, through `DebuggeeProcess.WaitForOutput`, which is both the right claim and faster:
the happy path returns in about 150ms instead of sleeping two seconds, and the suite lost 24 seconds.
Windows-only was never a debugger difference — it was the one platform slow enough to cross the
threshold. Assertions that require **silence** keep their fixed window, which is what that measures.

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
inside it, the registry entry is published, and `ci.yml`'s `registry` job keeps the two in step on every
release. See [RELEASING.md](RELEASING.md).
