# DotnetDebugger.Mcp

An MCP server that lets an AI assistant debug .NET programs: launch or attach to a process, set
breakpoints, step through code, inspect variables and evaluate expressions. It also carries a
searchable reference on how .NET debuggers work internally.

Published on NuGet as [`DotnetDebugger.Mcp`](https://www.nuget.org/packages/DotnetDebugger.Mcp).
Built on [SharpDbg](https://github.com/MattParkerDev/sharpdbg), driven through its debug adapter
surface.

> **Origin.** A fork of [decriptor/SharpDbg.MCP](https://github.com/decriptor/SharpDbg.MCP), now far
> enough from it to carry its own name: the debugger layer runs over the supported DAP surface rather
> than SharpDbg's internal API, launching a program under the debugger is new, and the two have not
> shared a commit since. This repository was called `SharpDbg.MCP` until the first release; GitHub
> redirects the old name.

## What it can do

- Attach to a running .NET process, or launch one and stop before its first line executes
- Set breakpoints by file and line or by method name, with conditions and hit counts
- Step over, into and out of code, and read the call stack for any thread
- Inspect local variables, expand objects member by member, and evaluate C# expressions
- Break on exceptions and read what was thrown, and read the debuggee's stdout and stderr
- Debug more than one process at once, each with its own breakpoints and stops
- Search embedded documentation on ICorDebug, the Debug Adapter Protocol and expression evaluation

## What it cannot do

Some of these need changes in the underlying debugger rather than here.

- **Distinguish handled from unhandled exceptions.** A stop does not say whether the program would
  have handled the exception, so breaking on throws is all or nothing.
- **Filter exception breaks by type.** The stop carries no type, so there is nothing to filter on
  before stopping. `get_exception_info` reads the type once stopped, which is after the fact.
- **Report the process id of a launched program.** It is `null` for a session created by
  `launch_program`, because the debugger reports nothing about the process it starts.
- **Report the exit code of a launched program.** It always reads as `0`, whatever the program
  returned.
- **Debug a self-contained single-file publish.** The runtime is packed inside the executable, so the
  debugger shim cannot find it to load the matching components. The attempt fails immediately with
  `CORDBG_E_DEBUG_COMPONENT_MISSING` (`0x80131C3C`), reported as
  `Attempting to register for runtime startup failed: -2146231236`. Self-contained on its own works,
  and single-file on its own works; only the combination does not.
- **Watch expressions**, **hot reload** and **data breakpoints** are not implemented.

## Requirements

- .NET 10 SDK or later
- An MCP-compatible client, such as Claude Code or Claude Desktop
- Windows, macOS or Linux

## Install

One package carries the native debugger shim for every platform, so the same configuration works
everywhere. There is nothing to clone or build.

**Claude Code**, for the current project:

```bash
claude mcp add dotnet-debugger -- dotnet tool exec DotnetDebugger.Mcp --yes
```

Add `--scope user` to make it available in every project, or `--scope project` to write a `.mcp.json`
that is committed and shared with your team.

**Claude Desktop**, by editing `~/.config/Claude/claude_desktop_config.json` on macOS and Linux, or
`%APPDATA%\Claude\claude_desktop_config.json` on Windows:

```json
{
  "mcpServers": {
    "dotnet-debugger": {
      "command": "dotnet",
      "args": ["tool", "exec", "DotnetDebugger.Mcp", "--yes"]
    }
  }
}
```

`dnx DotnetDebugger.Mcp --yes` is the shorter equivalent, and the form NuGet.org suggests. Prefer
`dotnet tool exec` in a client launched from a desktop environment rather than from a shell: `dnx`
lives in the SDK directory, which such a client often does not have on its `PATH`, while `dotnet`
reliably is.

A client only connects its MCP servers when a session starts, so restart it after changing the
configuration. To confirm the server is there, run `claude mcp list` or ask the client to list .NET
processes.

### Pinning a version

Installing the tool once avoids the resolution step on every start and keeps the version fixed until
you change it:

```bash
dotnet tool install -g DotnetDebugger.Mcp
```

The command is then `dotnet-debugger-mcp`, with no arguments. This needs `~/.dotnet/tools` on your
`PATH`, which is why it is not the default suggestion: a client that cannot find the command fails
the same way a wrong path does.

### Upgrading

`dotnet tool exec` can keep running a version it has already downloaded, even after a newer one is
published and indexed. Nothing looks wrong when it does: the server starts, the client reports it as
connected, and the only sign is that the new release's tools are missing.

Measured while releasing 0.1.1. With only 0.1.0 in the local package folder, the unpinned command kept
running 0.1.0, and clearing the NuGet HTTP cache did not change that. Naming the version once fetched
the new package, after which the unpinned command used it too:

```bash
dotnet tool exec DotnetDebugger.Mcp --version 0.1.1 --yes
```

Installing the tool avoids the question entirely, because then upgrading is explicit:

```bash
dotnet tool update -g DotnetDebugger.Mcp
```

To see which version is actually running, ask the client to list this server's tools, or read the
first line the server writes to stderr — it names the version at startup. A client's own health check
reports only that the process started, not what it is.

## A worked example

Catching a program before it has run a single line, which is the case attaching cannot reach:

```
User: "My app throws before it prints anything. Find out why."

Claude: [launch_program("/path/to/bin/Debug/net10.0/MyApp.dll")]
"Prepared, not running yet. Setting a breakpoint on the first line of Main."

Claude: [set_breakpoint("/path/to/Program.cs", 12)]
"Breakpoint set. It is unverified for now — nothing can bind before the program has
loaded its modules — and takes effect when the program starts."

Claude: [start_program()]
Claude: [wait_for_stop()]
"Stopped at Program.cs:12, before a single line has run."

Claude: [step_over(thread_id: 1)]
Claude: [get_variables(frame_id: 0)]
"configPath is null, and the next line passes it to File.ReadAllText."

Claude: [get_program_output()]
"The program printed nothing before the throw, which matches what you saw."
```

## Tools

### Debugging

| Tool | What it does |
|---|---|
| `list_dotnet_processes` | List the .NET processes running on this machine |
| `attach_to_process` | Attach the debugger to a running .NET process |
| `launch_program` | Prepare a program to run under the debugger, stopped before it starts |
| `start_program` | Run the program prepared by `launch_program` |
| `get_process_status` | Report whether the session is running, stopped, and where |
| `wait_for_stop` | Block until the debuggee stops, instead of polling |
| `get_program_output` | Read what the debuggee wrote to stdout and stderr |
| `detach_from_process` | Detach, leaving the process running |
| `list_sessions` | List open sessions and what each is debugging |
| `close_session` | Close a session, detaching first if needed |

### Breakpoints

| Tool | What it does |
|---|---|
| `set_breakpoint` | Set or update a breakpoint at a file and line, with an optional condition or hit count |
| `set_function_breakpoint` | Set a breakpoint on a method by name, when the file and line are not known |
| `remove_breakpoint` | Remove a breakpoint of either kind |
| `list_breakpoints` | List this session's breakpoints and whether each is verified |
| `set_exception_break_mode` | Control whether the debugger breaks when the program throws |

### Execution and inspection

| Tool | What it does |
|---|---|
| `continue_execution` | Resume until the next breakpoint or exit |
| `pause_execution` | Break into the debugger where the program currently is |
| `step_over`, `step_into`, `step_out` | Step by line, into a call, or out of the current method |
| `get_threads` | List the threads in the debuggee |
| `get_stack_trace` | Read the call stack for a thread |
| `get_variables` | Read the locals of a stack frame |
| `expand_variable` | Expand an object into its members, which may expand further |
| `evaluate_expression` | Evaluate a C# expression in the context of a frame |
| `get_exception_info` | Read the type, message, HResult, source and stack trace of what was thrown |

### Documentation

| Tool | What it does |
|---|---|
| `search_debugging_concepts` | Search the embedded documentation |
| `explain_icordebug_interface` | Explain a specific ICorDebug interface |
| `get_debugging_flow` | Walk through a debugging operation step by step |
| `list_debugging_concepts` | Browse the concept catalogue by category |

## Things worth knowing

### Breakpoints need portable PDBs

A breakpoint binds through the target's symbols, so the debuggee must be built with portable PDBs
sitting next to its assembly. A missing or mismatched PDB is the most common reason `set_breakpoint`
answers `verified: false` with `No symbols have been loaded for this document`.

Debug builds already do this. For a Release build, or any project that changes the defaults:

```xml
<PropertyGroup>
  <DebugType>portable</DebugType>
  <DebugSymbols>true</DebugSymbols>
</PropertyGroup>
```

Optimized code also moves locals out of reach, so `get_variables` is only fully useful with
`<Optimize>false</Optimize>`.

### macOS: headless environments may need an entitlement

macOS will not let a debugger take another process's task port unless the target carries
`com.apple.security.get-task-allow`. A program run through the `dotnet` muxer is fine, because the
muxer ships with that entitlement; an apphost produced by `dotnet publish` is ad-hoc signed with no
entitlements at all.

**On a desktop session this does not affect you.** Debugging a self-contained publish carrying no
entitlements is verified to work there.

It matters in a headless environment, such as a CI runner, and it fails badly when it does: macOS
refuses by blocking rather than returning an error, so the debugger stops responding and the call
never comes back. If a launch or attach hangs on macOS with no error at all, sign the target and try
again:

```bash
codesign -s - -f --entitlements get-task-allow.entitlements ./MyApp
```

with `get-task-allow.entitlements` containing:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.get-task-allow</key>
  <true/>
</dict>
</plist>
```

### Leave Just My Code on

`SHARPDBG_JUST_MY_CODE=false` works, but the first step out of your own code is slow.

With Just My Code on, a step keeps going until it reaches code you have symbols for, so it never
surfaces outside your own code. With it off, a step can stop in a module that has no symbols, and the
debugger decompiles that module to work out where it stopped. It then reports the location against
the decompiled source, which looks like
`decompiled/System.Private.CoreLib/.../DefaultInterpolatedStringHandler.cs:28`.

The first time costs real seconds. `System.Private.CoreLib`, which is where a step out of user code
lands first, took over twenty here, with the debuggee suspended throughout. The result is cached on
disk and the same step is immediate afterwards. If a step seems to hang, give it a minute before
concluding anything.

### Attaching to other users' processes

A debugger can read and change everything in the process it attaches to, so by default this server
attaches only to processes belonging to the user it runs as. `attach_to_process` refuses anything
else before it looks at the process at all, and `list_dotnet_processes` marks each entry with an
`owner` of `current_user`, `other_user` or `unknown`.

`unknown` is refused as well. On Windows that is what a system or elevated process looks like, since
its token cannot be opened, and treating it as your own would make the check decorative wherever the
lookup does not work.

`SHARPDBG_ALLOW_OTHER_USER_PROCESSES=true` lifts the restriction. The operating system still has its
own say: on Linux and macOS a normal user cannot attach to another user's process even with this
enabled, so in practice it matters when the server runs elevated or as root.

### Debugging more than one process

By default the server debugs one process at a time, and `SHARPDBG_MAX_SESSIONS` raises that. Each
session has its own process, breakpoints and stops.

`attach_to_process` and `launch_program` return a `session_id`. While only one session is open you
can ignore it, because every tool defaults to the only session. Taking on a second process opens a
second session rather than failing, and from then on `session_id` becomes required: with two
processes open, guessing which one a `continue_execution` was meant for would be worse than asking.

`detach_from_process` leaves the session open and free, so the next attach or launch reuses it rather
than taking another slot.

The default of one is deliberate. Every attach carries a risk of a native crash inside the debugging
shim, so more sessions means more exposure.

### How failures are reported

Every tool reports a failure the same way:

```json
{
  "success": false,
  "error": "Returned from a call to Continue that was not matched with a stopping event. (0x8013132F)",
  "explanation": "The process was already running, so there was nothing to resume. Check get_process_status before continuing."
}
```

`error` is whatever the debugger said, kept verbatim. `explanation` says what the failure means and
what to do about it, for the `CORDBG_E_*` results the debugger raises: a process that has exited, an
operation that needs the debuggee stopped, a variable that is not live at this instruction, a frame id
from an earlier stop, another debugger already attached. It is `null` for failures that are not the
debugger's, such as invalid arguments.

## Configuration

Set these as environment variables in your client's configuration:

```json
{
  "mcpServers": {
    "dotnet-debugger": {
      "command": "dotnet",
      "args": ["tool", "exec", "DotnetDebugger.Mcp", "--yes"],
      "env": {
        "SHARPDBG_LOG_LEVEL": "Debug"
      }
    }
  }
}
```

| Variable | Description | Default |
|---|---|---|
| `SHARPDBG_LOG_LEVEL` | `Trace`, `Debug`, `Information`, `Warning`, `Error` or `Critical` | `Information` |
| `SHARPDBG_MAX_SESSIONS` | Sessions open at once, each debugging its own process | `1` |
| `SHARPDBG_OPERATION_TIMEOUT_SECONDS` | Bounds attaching, starting, pausing and closing a session. Steps and reads are not bounded | `30` |
| `SHARPDBG_EVAL_TIMEOUT_MS` | Bounds anything that runs code in the debuggee: `evaluate_expression` and `get_exception_info`. Minimum 100 | `5000` |
| `SHARPDBG_BREAKPOINT_BIND_TIMEOUT_MS` | How long to wait for a breakpoint to bind before reporting it unverified, minimum 100 | `2000` |
| `SHARPDBG_JUST_MY_CODE` | Restrict debugging to your own code. See above before turning this off | `true` |
| `SHARPDBG_ALLOW_OTHER_USER_PROCESSES` | Allow attaching to processes not owned by the current user | `false` |
| `SHARPDBG_ENABLE_DIAGNOSTICS` | Detailed diagnostic logging | `false` |

## Troubleshooting

**The server does not appear in the client.** Check that the path in the configuration is absolute
and correct, then fully quit and restart the client. Claude Desktop logs to `~/Library/Logs/Claude/`
on macOS and `%APPDATA%\Claude\logs\` on Windows.

**Attaching fails.** The usual causes are that the process is owned by another user (see above), has
already exited, is not a .NET process, or already has a debugger attached. `list_dotnet_processes`
shows what the server can see and who owns it.

**A breakpoint is not hit.** Check that it came back `verified: true`. If not, the PDB is the first
thing to look at. If it is verified and still not hit, confirm the line is actually reached and that
the file path matches the one the PDB records.

**`list_dotnet_processes` comes back empty on macOS or Linux.** Module enumeration needs permissions
there, and the server falls back to detection by process name, which misses programs whose apphost is
renamed.

**A launch or attach hangs on macOS with no error.** See the entitlement section above.

To see what the server is doing, set `SHARPDBG_LOG_LEVEL=Trace` and
`SHARPDBG_ENABLE_DIAGNOSTICS=true`, or run it directly and watch stderr:

```bash
dotnet tool exec DotnetDebugger.Mcp --yes
```

## Development

```bash
git clone https://github.com/nevse/dotnet-debugger-mcp.git
cd dotnet-debugger-mcp
dotnet build
dotnet test
```

The integration tests drive a real debuggee with real breakpoints, so they are slower than the rest
and are separated by `TestCategory=Integration`.

To point a client at your working copy instead of the published package:

```bash
claude mcp add dotnet-debugger -- dotnet run --project "$(pwd)/src/SharpDbg.MCP/SharpDbg.MCP.csproj"
```

[CONTRIBUTING.md](CONTRIBUTING.md) covers the layout and conventions.
[docs/RELEASING.md](docs/RELEASING.md) covers cutting a release, including the nuget.org
trusted-publishing policy that the workflow depends on but does not show.

## Related projects

- [SharpDbg](https://github.com/MattParkerDev/sharpdbg) — the underlying .NET debugger
- [ClrDebug](https://github.com/lordmilko/ClrDebug) — ICorDebug API wrapper
- [Model Context Protocol](https://github.com/modelcontextprotocol) — the specification and SDKs

## License

[MIT](LICENSE)
