# Upstream defects we are waiting on

Defects outside this repository that limit what this server can do. None of them are work we can
finish ourselves, so they are kept out of the backlog; the point of this file is that the evidence is
written down once and a fix can be verified without re-deriving it.

Defect 1 was reported as #24 and **fixed in SharpDbg 0.1.8**, which this repository now uses. The
maintainer also answered two other points in that thread, recorded under defects 2 and 9. The rest are
still unreported; the drafts at the bottom are ready to send.

## Checking whether a fix has landed

SharpDbg is a package, so a fix arrives as a version bump:

```bash
dotnet list package --outdated          # is there a newer SharpDbg than 0.1.8?
```

Bump `SharpDbg` in `Directory.Packages.props`, then run the full suite. Two tests exist purely to
tell us when a limitation is gone — **they are supposed to start failing:**

| test | defect | what it failing means |
|---|---|---|
| `ExpandVariable_MemberNeedingEvaluation_StillNeedsASecondContinue` | 2 | one continue resumes the debuggee after an evaluation; drop the warnings in `expand_variable` and `evaluate_expression` |
| `EvaluateExpression_DoesNotYetYieldAnExpandableReference` | 3 | evaluation results can be expanded; say so in `expand_variable`'s description |

This is how defect 1 was found to be fixed: the test that pinned it started failing on 0.1.8, saying
`Continue unexpectedly succeeded after the poisoning expansion`.

Two defects have no test watching them:

- **4** is hidden by `DebugSession.RetryWhileModulesLoad`. To check it, look at whether
  `ManagedDebugger._modules` is still an unguarded `Dictionary`; if it is guarded, delete the retry.
- **5** and **8** are races that show up as an occasional stuck or aborted test run rather than a
  reproducible failure. When **8** is fixed, delete the retry loop in the `Integration tests` step of
  `.github/workflows/ci.yml`, which exists only because of it.

## SharpDbg (https://github.com/MattParkerDev/sharpdbg, MIT)

Everything below was confirmed on 0.1.7 both by decompiling the package and by probing a live
session.

### 1. Expanding an evaluated member leaves a disposed handle — FIXED in 0.1.8

Reported as [#24](https://github.com/MattParkerDev/sharpdbg/issues/24), fixed by
[fd8de64](https://github.com/MattParkerDev/sharpdbg/commit/fd8de64a67ae7ae6bb9129d92acd1800d2bdf07d).
Expanding a member whose value needed function evaluation used to leave the variable manager holding a
released handle, after which every `Continue` threw `Handle has been disposed. (0x80131C01)` and only
`Disconnect` released the debuggee. `Continue` now succeeds - though see defect 2 for what is left.

The standalone reproduction at https://github.com/nevse/sharpdbg-bug-handle-disposed-on-continue is
pinned to 0.1.7 and still reproduces there.

### 2. Any successful function evaluation leaves the debuggee unable to resume — still open on 0.1.8

The same area as #24 but wider: it is not about variable expansion. Any evaluation that runs code in
the target leaves the process needing a second `Continue`, and a third then fails with
`CORDBG_E_SUPERFLOUS_CONTINUE`. The first `Continue` reports success, so the process looks running
while it is suspended.

Mentioned in #24, where the maintainer said he could not reproduce it and asked whether it was still
happening. Re-measured on 0.1.8, and it is - unchanged from 0.1.7 except that the disposed-handle
exception is gone, which makes it **quieter rather than better**: before, a caller at least got an
error, and now it is told the process resumed.

| what happened before the continue | 0.1.7 | 0.1.8 |
|---|---|---|
| exception stop, no evaluation | 1, clean | 1, clean |
| breakpoint stop, an evaluation that failed | 1, clean | 1, clean |
| breakpoint stop, `point.ToString()` | 2, then `SUPERFLOUS_CONTINUE` | 2, then `SUPERFLOUS_CONTINUE` |
| exception stop, `ManagedDebugger.ExceptionInfo` (4 property reads) | never resumed | never resumed |
| expanding a record's `EqualityContract`, then one `Continue` | threw `0x80131C01` | returns true, process stays suspended |

The contrast between the second and third rows is the useful part for whoever fixes it: an evaluation
that **fails** leaves the process able to resume on one continue, and one that **succeeds** does not.

**Blocks:** `evaluate_expression` and `expand_variable` are affected, and this is why there is no
`get_exception_info` — the upstream `ExceptionInfo` reads Message, HResult, Source and StackTrace
through property getters, so retrieving exception details costs the session. Both tool descriptions
say a second `continue_execution` may be needed. Automating that second continue was considered and
not done: a continue issued when it was not needed would resume past a stop the caller asked for, and
the number needed is not predictable - two after one evaluation, more than two after four.

### 3. Evaluation results carry no variables reference — not reported

`ManagedDebugger.Evaluate` hardcodes `variablesReference` to 0 on every path, so the result of
`evaluate_expression` is only ever a string, while entries from `get_variables` can be walked with
`expand_variable`. The fix is to register the evaluated `ICorDebugValue` with the variable manager
the way scope members already are.

**Blocks:** `expand_variable` only works on variables, not on evaluation results.

### 4. The modules dictionary is not thread safe — not reported

`_modules` is a plain `Dictionary` written by the module-load callback on the debugger's own thread
and enumerated on the caller's thread while breakpoints bind. A breakpoint set while the debuggee is
still loading assemblies fails with:

```
System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
   at System.Collections.Generic.Dictionary`2.ValueCollection.Enumerator.MoveNext()
   at SharpDbg.Infrastructure.Debugger.ManagedDebugger.SetFunctionBreakpoints(...)
```

Line breakpoints reach the same enumeration through `TryBindBreakpoint`, so both kinds are exposed;
function breakpoints hit it more often because they enumerate every module for every request.

**Blocks:** nothing now — `DebugSession.RetryWhileModulesLoad` retries these calls, which is safe
only because both `SetBreakpoints` and `SetFunctionBreakpoints` replace a whole set.

### 5. A hit on a breakpoint that was just replaced freezes the debuggee — not reported

`HandleBreakpoint` ends with:

```csharp
var managedBreakpoint = _breakpointManager.FindByCorBreakpoint(functionBreakpoint);
ArgumentNullException.ThrowIfNull(managedBreakpoint, "managedBreakpoint");
```

`SetBreakpoints` deactivates a file's `ICorDebugFunctionBreakpoint` objects and drops the
`BreakpointInfo` that owned them. A hit already in flight on one of those then finds nothing, and this
throws on the debugger's callback thread - which never continues the process and never raises a stop.
The debuggee is left suspended with the session still believing it runs, and only `Disconnect`
releases it. A few lines above, an unrecognised breakpoint type is handled by continuing instead,
which is what this path should do too.

Seen once in CI on macOS, in a test that sets two breakpoints in a file, removes one, and waits for
the other: the log shows the survivor bound and verified, then nothing for 30 seconds. The debuggee
hits the line being replaced every 150ms, which is what makes the window reachable at all.

**Blocks:** nothing outright, but every `set_breakpoint` and `remove_breakpoint` re-sends the file's
whole set - that is forced by the replace semantics - so each call on a running debuggee is a chance
to hit this. There is nothing safe to do here: pausing around the re-send would trade this race for a
stop the caller did not ask for.

### 6. Decompiled source locations cannot be produced at all — not reported

`OnStopped2` carries a `DecompiledSourceInfo` for stops in modules with no symbols of their own, which
the debugger decompiles to get a location. It is never populated, because the path that would build it
fails in three different ways before it gets there, and each failure is caught and logged as
`Error handling event StepCompleteCorDebugManagedCallbackEventArgs` - after which the callback neither
continues the process nor reports a stop, so the debuggee is left suspended while the session still
believes it runs. The step is retried, so it fails again forever.

Traced by stepping with `JustMyCode` off, which is required for any of this to be attempted:

1. **The decompiler is not a declared dependency.** The path needs
   `ICSharpCode.Decompiler 10.1.1.8388`, which the nuspec does not list, so it fails with
   `FileNotFoundException: Could not load file or assembly 'ICSharpCode.Decompiler'`. Adding that
   package to the consuming project gets past this - it is on nuget.org - but only reveals the next
   failure, so this repository does not carry the dependency.
2. **Decompiling System.Private.CoreLib throws.** With the decompiler present, `GeneratePdb` fails
   with `NullReferenceException` inside itself. This is the module any step out of user code lands in
   first, through string interpolation or `Thread.Sleep`.
3. **A no-symbols assembly that counts as user code is refused.** Building a library with
   `<DebugType>none</DebugType>` and stepping into it - the case this feature exists for - hits
   upstream's own guard, `InvalidOperationException: The module we are decompiling is user code - this
   should never happen`. The JIT flags make an unoptimized assembly user code whether or not it has a
   PDB.

There is a fourth problem underneath: a failed attempt leaves a truncated `.decompiled.pdb` in
`%LOCALAPPDATA%/Temp/SharpIdeSymbolCache`, and every later step reports
`could not load cached PDB` and fails again until that directory is deleted by hand.

**Blocks:** a stop in code without symbols reports no location at all, and with `JustMyCode` off a
step that reaches such code freezes the debuggee. `JustMyCode` defaults to true here, which avoids the
freeze by never attempting the decompilation. Wiring the field through into `get_process_status` was
tried and reverted: with nothing able to produce it, that is a field that is always null and a code
path no test can reach. It is a handful of lines to add back when this works - capture the sixth
argument of `OnStopped2` in `OnDebuggerStoppedAtLocation`, put it on `ExecutionState`, and report the
type, the assembly and whether the file exists on disk, since the path names a document inside a
generated PDB rather than a real file.

### 7. An exception stop says neither the type nor whether it will be handled — not reported

`HandleException` discards the callback's event type, so a stop cannot be classified as first-chance
or unhandled, and the exception's type can only be read by running code in the target, which hits
defect 2.

**Blocks:** `set_exception_break_mode` can only be all or nothing. No unhandled-only mode, which is
what a debugger normally defaults to, and no filtering by exception type.

### Not a defect: a breakpoint is reported unverified and then verified

Recorded because it looked like one from here, and the maintainer explained otherwise in #24: the
two-phase report is intentional and matches netcoredbg and vsdbg. After an attach the debugger sends a
breakpoint event with `verified: false` for every breakpoint, so an IDE can show they are not bound
yet, and a second event with `verified: true` once each one binds.

He also pointed out the intended order, which is worth knowing: `AttachRequest` does not attach -
`ConfigurationDone` does - so a DAP client sets its initial breakpoints *between* the two, and never
sees the unverified phase at all.

An MCP server cannot do that. `attach_to_process` and `set_breakpoint` are separate tool calls, and
nothing knows the breakpoints at attach time, so `ConfigurationDone` has already run by the time the
first one arrives. `DebugSession` waits for the verified event instead, bounded by
`SHARPDBG_BREAKPOINT_BIND_TIMEOUT_MS`, which is why a breakpoint set immediately after attaching still
comes back verified.

## .NET runtime

### 8. libmscordbi segfaults while replaying attach events

A test run occasionally dies with `Test Run Aborted`, no failing test, and the log cut off mid-line.
The macOS crash reports put it in the debugger shim, on its own event thread:

```
SIGSEGV in libmscordbi.dylib
  ShimProcess::QueueFakeAssemblyAndModuleEvent(ICorDebugAssembly*)
  ShimProcess::QueueFakeAttachEvents()
  ShimProcess::QueueFakeAttachEventsIfNeeded(bool)
  CordbRCEventThread::FlushQueuedEvents(CordbProcess*)
  CordbRCEventThread::ThreadProc()
```

That is the synthetic assembly and module load events being queued for a fresh attach. Nothing of
ours is on the stack, and it reproduces at 26b4626, before any of the work that first surfaced it.

Roughly one full run in six to eight on macOS arm64 with .NET 10.0.0, where the integration tests
attach and detach around thirty times in one test host. The rate depends on how soon after the
debuggee starts the debugger attaches: adding six more attaches took it to three runs in six, and
waiting half a second before attaching (`DebuggeeProcess.AttachSettleTime`) brought it back to one in
eight. Nothing in-process can do better, since the shim dies on a thread we do not own.

A server that attaches once per session is far less exposed than the test suite.

Measured rate, all on macOS arm64 with the integration suite as it stands: 4 aborted runs in 12, and
1 in 8 in an earlier sample - so somewhere around a fifth to a quarter of runs, which is what makes
the macOS CI job red so often. Ubuntu and Windows have not aborted once.

On CI it twice died at exactly the same point, on the seventeenth test's attach; locally the crash
lands in different places (after 8, 16, 31, 40 and 46 tests in different runs), so the boundary is
not a property of any one test.

Things that did **not** help, so they do not need trying again:

- Forcing the previous session's `CordbProcess` to finalize before the next attach
  (`GC.Collect()` plus `WaitForPendingFinalizers()` when the debuggee is disposed): 4 aborts in 12
  runs, no better than without it. The first 8 runs were clean, which is exactly how a 20% failure
  rate looks if you stop measuring early.
- Blaming the test that poisons the debugger with a disposed handle: it is two tests before the
  boundary CI died at, but the poisoning test plus one attach after it ran 8 times without an abort.

What did help, a little, was waiting after the debuggee starts before attaching
(`DebuggeeProcess.AttachSettleTime`), which took it from three runs in six back to one in eight.

**Before reporting to dotnet/runtime:** check whether it also happens on Linux and Windows, and
reduce it to a repro that only attaches and detaches in a loop.

## Drafts, ready to send

### Comment on #24, for defect 2

The maintainer asked whether the second problem is still happening. It is - re-measured on 0.1.8:

> Thanks, 0.1.8 fixes the disposed handle - `Continue` no longer throws after expanding
> `EqualityContract`.
>
> The second problem is still there, and the fix made it quieter rather than better: `Continue` now
> returns success, but the debuggee does not actually resume until a second `Continue`. A caller is
> told the process is running while it is suspended.
>
> Measured on 0.1.8 with a debuggee that prints a line every 150ms, counting the lines to tell whether
> it really resumed: [the 0.1.8 column of the table in defect 2]
>
> The useful contrast is the middle two rows: an evaluation that **fails** leaves the process able to
> resume on one continue, one that **succeeds** does not. `ExceptionInfo` is worth a look too - its
> four property reads leave the process unable to resume at all, which is why I have not exposed
> exception details in my server.

### New issue, for defect 4

> **Title:** Breakpoint calls race with module loading: `_modules` is enumerated while the load
> callback writes to it
>
> A breakpoint set while the debuggee is still loading assemblies fails with
> `InvalidOperationException: Collection was modified` from inside `SetFunctionBreakpoints`
> [stack from defect 4]. `_modules` is a plain `Dictionary`; `HandleModuleLoaded` writes to it on the
> runtime callback thread while `SetFunctionBreakpoints` and `TryBindBreakpoint` enumerate it on the
> caller's. Guarding it, or binding against a snapshot, would fix both paths.
