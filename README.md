# SharpDbg MCP Server

An MCP (Model Context Protocol) server that exposes .NET debugging capabilities and comprehensive debugger documentation to AI assistants like Claude.

## ⚠️ Project Status

**BETA - Feature Complete**

- ✅ **Phase 1 (Documentation)**: Fully functional - Search comprehensive .NET debugger documentation
- ✅ **Phase 2 (Debugging)**: Functional - Complete debugging workflow with breakpoints, stepping, variable inspection, and expression evaluation

## What is This?

SharpDbg MCP Server provides two main capabilities:

1. **Documentation Access**: Search and explore comprehensive documentation about .NET debugger internals, including ICorDebug API, Debug Adapter Protocol (DAP), expression evaluation, and debugging workflows.

2. **Interactive Debugging**: Attach to .NET processes and perform debugging operations through MCP tools (in development).

## Why Use This?

- **AI-Assisted Debugging**: Enable AI assistants to help debug .NET applications by giving them access to process state
- **Learning Resource**: Interactive access to comprehensive debugger documentation during conversations
- **Novel Capability**: The only MCP server currently offering .NET debugging capabilities

## Prerequisites

- .NET 10 SDK or later
- Claude Desktop or another MCP-compatible client
- macOS, Linux, or Windows

## Installation

### 1. Clone and Build

```bash
git clone https://github.com/nevse/SharpDbg.MCP.git
cd SharpDbg.MCP
dotnet build src/SharpDbg.MCP/SharpDbg.MCP.csproj
```

### 2. Register the server

The server path must be absolute. Running this from the repository root fills it in for you, which
avoids the most common installation failure: a configuration file that still holds a placeholder
path, leaving a server that is listed but never starts.

**Claude Code**, for the current project:

```bash
claude mcp add sharpdbg -- dotnet run --project "$(pwd)/src/SharpDbg.MCP/SharpDbg.MCP.csproj"
```

Add `--scope user` to make it available in every project, or `--scope project` to write a `.mcp.json`
that is committed and shared with your team.

**Claude Desktop**, by hand:

**macOS/Linux**: `~/.config/Claude/claude_desktop_config.json`
**Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "sharpdbg": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/SharpDbg.MCP/src/SharpDbg.MCP/SharpDbg.MCP.csproj"
      ]
    }
  }
}
```

Replace `/absolute/path/to/SharpDbg.MCP` with the directory you cloned into — running `pwd` at the
repository root prints it.

### 3. Restart the client

MCP servers are connected when a session starts, so a client that is already running will not pick up
the new server until it is restarted.

### 4. Verify

Ask the client to list .NET processes, or run `claude mcp list`. If the server does not appear, the
path in the configuration is the first thing to check.

## Attaching to other users' processes

A debugger can read and change everything in the process it attaches to, so by default this server
attaches only to processes belonging to the user it runs as. `attach_to_process` refuses anything
else before it looks at the process at all, and `list_dotnet_processes` marks each entry with an
`owner` of `current_user`, `other_user` or `unknown`.

`unknown` is refused as well. On Windows that is what a system or elevated process looks like, since
its token cannot be opened; treating it as your own would make the whole check decorative wherever
the lookup does not work.

Set `SHARPDBG_ALLOW_OTHER_USER_PROCESSES=true` to lift the restriction. Note that the operating
system has its own say regardless: on Linux and macOS a normal user cannot attach to another user's
process even with this enabled, so in practice it matters when the server runs elevated or as root.

## Debugging more than one process

By default the server debugs one process at a time. `SHARPDBG_MAX_SESSIONS` raises that, and each
session has its own process, its own breakpoints and its own stops.

`attach_to_process` returns a `session_id`. While only one session is open you can ignore it — every
tool defaults to the only session, exactly as before. Attaching a second process opens a second
session instead of failing, and from then on `session_id` becomes **required**: with two processes
open, guessing which one a `continue_execution` was meant for would be worse than asking for the id.

```
list_sessions                     → which sessions exist, and what each is attached to
close_session(session_id)         → detach and free the slot
```

`detach_from_process` leaves the session open but unattached, so the next `attach_to_process` reuses it
rather than taking another slot. Reaching the limit is reported with what to do about it:

```
Maximum number of concurrent debug sessions (1) reached.
Close one with close_session, or raise SHARPDBG_MAX_SESSIONS.
```

The default of one is deliberate: every attach carries a risk of the shim crash in
[docs/UPSTREAM.md](docs/UPSTREAM.md) defect 8, so more sessions means more exposure. Two concurrent
sessions are verified to stay isolated — resuming one leaves the other where it was — by
`TwoSessions_DebugTwoProcessesIndependently`.

## Breakpoints need portable PDBs

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

## Configuration

The server can be configured using environment variables. Add them to your Claude Desktop configuration:

```json
{
  "mcpServers": {
    "sharpdbg": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/SharpDbg.MCP/src/SharpDbg.MCP/SharpDbg.MCP.csproj"],
      "env": {
        "SHARPDBG_LOG_LEVEL": "Information",
        "SHARPDBG_OPERATION_TIMEOUT_SECONDS": "30",
        "SHARPDBG_EVAL_TIMEOUT_MS": "5000",
        "SHARPDBG_ENABLE_DIAGNOSTICS": "false"
      }
    }
  }
}
```

### Available Configuration Options

| Environment Variable | Description | Default | Valid Values |
|---------------------|-------------|---------|--------------|
| `SHARPDBG_LOG_LEVEL` | Logging verbosity | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` |
| `SHARPDBG_MAX_SESSIONS` | Debug sessions open at once, each debugging its own process | `1` | Positive integer |
| `SHARPDBG_OPERATION_TIMEOUT_SECONDS` | Timeout for debugging operations | `30` | Positive integer |
| `SHARPDBG_ALLOW_OTHER_USER_PROCESSES` | Allow attaching to processes not owned by the current user, including ones whose owner cannot be determined | `false` | `true`, `false` |
| `SHARPDBG_EVAL_TIMEOUT_MS` | Expression evaluation timeout in milliseconds | `5000` | ≥ 100 |
| `SHARPDBG_BREAKPOINT_BIND_TIMEOUT_MS` | How long to wait for a breakpoint to bind before reporting it as unverified | `2000` | ≥ 100 |
| `SHARPDBG_JUST_MY_CODE` | Restrict debugging to user code, skipping framework and third-party assemblies. **Leave this on** — see below | `true` | `true`, `false` |
| `SHARPDBG_ENABLE_DIAGNOSTICS` | Enable detailed diagnostic logging | `false` | `true`, `false` |

### Do not turn off Just My Code

`SHARPDBG_JUST_MY_CODE=false` currently freezes the debuggee, and it is worth knowing why before
reaching for it.

With Just My Code on, a step never leaves your own code. With it off, a step can land in a module that
has no symbols — the framework, or any assembly shipped without a PDB — and the debugger then tries to
decompile that module to work out where it stopped. Every route through that decompilation fails in
SharpDbg 0.1.7: the decompiler is not a declared dependency of the package, decompiling
`System.Private.CoreLib` throws, and an assembly without a PDB that still counts as user code is
refused outright. The failure is caught and logged, after which the step never completes and nothing
is reported, so the process stays suspended and only `detach_from_process` releases it. A half-written
PDB is also left in `%LOCALAPPDATA%/Temp/SharpIdeSymbolCache`, which makes every later step fail the
same way until that directory is deleted.

The details are in [docs/UPSTREAM.md](docs/UPSTREAM.md) defect 6, along with what changes here once it
is fixed upstream. Until then, stopping in code without symbols reports no location at all — which is
a limitation, not a hang.

### Examples

**Enable debug logging:**
```json
"env": {
  "SHARPDBG_LOG_LEVEL": "Debug"
}
```

**Increase operation timeout for slow systems:**
```json
"env": {
  "SHARPDBG_OPERATION_TIMEOUT_SECONDS": "60"
}
```

**Enable full diagnostics for troubleshooting:**
```json
"env": {
  "SHARPDBG_LOG_LEVEL": "Trace",
  "SHARPDBG_ENABLE_DIAGNOSTICS": "true"
}
```

## Available Tools

### Documentation Tools (Phase 1 - Fully Functional)

#### `search_debugging_concepts`
Search the embedded documentation for debugging concepts.

```
Example: "How does expression evaluation work in .NET debuggers?"
```

**Parameters:**
- `query` (string): Search query for concepts like "ICorDebug", "breakpoints", "stepping"

**Returns:** JSON with matching documentation sections, including titles, content previews, and full text.

#### `explain_icordebug_interface`
Get detailed information about specific ICorDebug interfaces.

```
Example: "Explain ICorDebugEval"
```

**Parameters:**
- `interface_name` (string): Interface name like "ICorDebugEval", "ICorDebugProcess"

**Returns:** JSON with interface explanation and usage details.

#### `get_debugging_flow`
Retrieve step-by-step flows for common debugging operations.

```
Example: "Show me the flow for setting a breakpoint"
```

**Parameters:**
- `operation` (string): Operation like "setting a breakpoint", "evaluating an expression", "stepping"

**Returns:** Complete flow diagram with detailed steps.

#### `list_debugging_concepts`
Browse all available debugging concepts organized by category.

**Parameters:** None

**Returns:** JSON with all concepts grouped by category (Core Architecture, Debugging Fundamentals, Expression Evaluation, etc.)

### Debugging Tools (Phase 2 - Functional)

#### `list_sessions`
List the open debug sessions, which is how you find the `session_id` the other tools take.

**Parameters:** None

**Returns:** Each session's ID, the process it is attached to, whether it is running or stopped, and
where it stopped. Plus `max_sessions`, the configured limit.

#### `close_session`
Close a session, detaching from its process if it is still attached.

**Parameters:**
- `session_id` (int): ID from `attach_to_process` or `list_sessions`

**Returns:** Success/error response.

#### `list_dotnet_processes`
List all .NET processes currently running on the system.

**Parameters:** None

**Returns:** JSON array of processes with ID, name, main module path, and `owner`, which is
`current_user`, `other_user` or `unknown` — see [Attaching to other users'
processes](#attaching-to-other-users-processes).

#### `attach_to_process`
Attach the debugger to a .NET process.

**Parameters:**
- `process_id` (int): Process ID to attach to
- `session_id` (int, optional): Attach in this existing session rather than picking one

**Returns:** Success/error response including the `session_id` to pass to the other tools. Attaching
while another process is already being debugged opens a second session — see [Debugging more than one
process](#debugging-more-than-one-process).

#### `get_process_status`
Check the status of the current debug session.

**Parameters:** None

**Returns:** Session state including attachment status, process ID, and execution state.

#### `detach_from_process`
Detach the debugger from the current process.

**Parameters:** None

**Returns:** Success/error response.

#### `wait_for_stop`
Wait for the debuggee to stop, instead of calling `get_process_status` in a loop.

**Parameters:**
- `timeout_ms` (int, optional): How long to wait, default `10000`, maximum `300000`

**Returns:** The same fields as `get_process_status`, plus `stopped`. A `stopped` of `false` means the
process was still running when the wait expired, which is not an error — call again to keep waiting.
A `stop_reason` of `exited` means the process is gone and cannot be stepped or continued.

#### `set_exception_break_mode`
Control what happens when the debuggee throws.

**Parameters:**
- `mode` (string): `always` (default) or `never`

**Returns:** The mode in effect, plus how many exception stops have been seen and how many were
resumed automatically.

The debugger stops on **every first-chance exception**, including ones the program catches itself, so
a program that uses exceptions routinely suspends constantly. `always` keeps those stops:
`stop_reason` is `exception`, and `get_stack_trace` on `stopped_thread_id` shows where it was thrown
(exception stops carry no `current_location`). `never` resumes them automatically, which is what you
want when hunting something else in a program whose own exceptions are noise — breakpoints still
stop normally.

There is no mode for unhandled exceptions only, and no filtering by exception type. The debugger
reports neither the type nor whether the program will handle the exception without running code in
the target, which currently leaves the process unable to resume.

#### `set_breakpoint`
Set a breakpoint at a specific file and line number, or update the conditions of an existing one.

**Parameters:**
- `file_path` (string): Absolute path to source file
- `line` (int): Line number (1-based)
- `condition` (string, optional): C# expression evaluated in the frame; the process only stops when it is true
- `hit_condition` (string, optional): Hit count in the form `5`, `==5`, `>5`, `>=5`, `<5`, `<=5` or `%5` (every 5th hit)

Hit counts include hits where `condition` was false, and are reset whenever any breakpoint in the
same file is added, changed or removed. Calling this again for the same file and line replaces both
conditions, so omitting one clears it.

**Returns:** Breakpoint information including ID, verification status, and message.

**`verified: false`** means the breakpoint is not bound. Usually the path or the line does not exist
in the target — see [Breakpoints need portable PDBs](#breakpoints-need-portable-pdbs) — but a
breakpoint in an assembly that has not been loaded yet binds by itself once it loads, so check
`list_breakpoints` again rather than setting it repeatedly.

#### `set_function_breakpoint`
Set a breakpoint on a method by name, for when the method is known but the file and line are not.

**Parameters:**
- `function_name` (string): `Method`, `Type.Method` or `Namespace.Type.Method`
- `condition` (string, optional): as for `set_breakpoint`
- `hit_condition` (string, optional): as for `set_breakpoint`

**Returns:** Breakpoint information including ID, verification status, and `bound_locations`.

The type part matches by suffix, so `Program.Work` matches `MyApp.Program.Work`. **Every** method
matching the name binds, which includes overloads and same-named methods in several assemblies —
`bound_locations` reports each place it bound, so check it to see what was actually caught. Narrow it
with a parameter list, `Work(int, string)`, where C# keywords, nullables and generics are understood
(`int`, `string?`, `List<int>`), or by generic arity, `Work<T>`.

Like line breakpoints this needs portable PDBs. `verified: false` with `No functions matching` means
the name matched nothing in the modules loaded so far, and it will bind by itself if a later assembly
contains it. Re-sending resets the hit counts of all function breakpoints.

#### `remove_breakpoint`
Remove a previously set breakpoint, of either kind.

**Parameters:**
- `breakpoint_id` (int): ID returned by `set_breakpoint` or `set_function_breakpoint`

**Returns:** Success/error response. Removing a breakpoint resets the hit counts of the other
breakpoints in the same file, or of all function breakpoints.

#### `list_breakpoints`
List every breakpoint set in this session, with its current verification status.

**Parameters:** None

**Returns:** `breakpoints`, with ID, file, line, verification status and conditions, and
`function_breakpoints`, with ID, name, verification status, `bound_locations` and conditions.

#### `get_threads`
Get all threads in the attached process.

**Parameters:** None

**Returns:** JSON array of threads with ID and name.

#### `get_stack_trace`
Get the call stack for a specific thread.

**Parameters:**
- `thread_id` (int): Thread ID to query

**Returns:** Array of stack frames with source locations, line numbers, and frame IDs.

#### `get_variables`
Get local variables for a specific stack frame.

**Parameters:**
- `frame_id` (int): Stack frame ID from get_stack_trace

**Returns:** Array of variables with names, values, types, and references for expansion.

#### `expand_variable`
Expand a `variables_reference` into its members, which may themselves carry references to expand
further. Works only while the process is stopped.

**Parameters:**
- `variables_reference` (int): Reference from `get_variables` or from this tool

**Returns:** Array of members with names, values, types, and further references.

> **Warning:** expanding a member whose value has to be evaluated in the process — a record's
> `EqualityContract`, or anything else of type `RuntimeType` — currently leaves the debugger holding
> a disposed handle, after which `continue_execution` always fails and the process stays suspended.
> Only `detach_from_process` releases it. This is a SharpDbg defect, reported as
> [MattParkerDev/sharpdbg#24](https://github.com/MattParkerDev/sharpdbg/issues/24). Prefer expanding
> your own data.

#### `continue_execution`
Resume process execution until next breakpoint or exit.

**Parameters:** None

**Returns:** Success confirmation.

#### `pause_execution`
Pause execution and break into the debugger at the current location.

**Parameters:** None

**Returns:** Success confirmation.

#### `step_over`
Step over the current line (execute and stop at next line in same method).

**Parameters:**
- `thread_id` (int): Thread to step

**Returns:** Success confirmation.

#### `step_into`
Step into the current line (enter called methods).

**Parameters:**
- `thread_id` (int): Thread to step

**Returns:** Success confirmation.

#### `step_out`
Step out of the current method (execute until return).

**Parameters:**
- `thread_id` (int): Thread to step

**Returns:** Success confirmation.

#### `evaluate_expression`
Evaluate a C# expression in the context of a stack frame.

**Parameters:**
- `expression` (string): C# expression to evaluate (e.g., "user.Name", "x + y")
- `frame_id` (int): Stack frame ID for evaluation context

**Returns:** Evaluation result with value, type, and variables reference.

> **Warning:** an expression that has to run code in the target — a property getter, `ToString()`,
> any method call — currently leaves the debuggee needing a second `continue_execution` before it
> really resumes, and after several such evaluations it may not resume at all. The first
> `continue_execution` still reports `resumed: true`, so the process looks running while it is
> suspended. Reading fields through `get_variables` and `expand_variable` does not have this problem.
> This is a SharpDbg defect, related to
> [MattParkerDev/sharpdbg#24](https://github.com/MattParkerDev/sharpdbg/issues/24).

### Limitations & Planned Features

**Current Limitations (Require Upstream SharpDbg Changes):**
- **Running code in the target suspends the debuggee** - any evaluation of a property, `ToString()`
  or method call, and expanding a member that needs one; see the warnings under
  `evaluate_expression` and `expand_variable`
  ([MattParkerDev/sharpdbg#24](https://github.com/MattParkerDev/sharpdbg/issues/24))
- **Break on unhandled exceptions only** - a stop does not say whether the program will handle the
  exception, so exception breaks can only be all or nothing (`set_exception_break_mode`)
- **Filtering exception breaks by type** - reading the exception's type needs code to run in the
  target, which hits the defect above
- **Expanding an evaluation result** - `evaluate_expression` never returns a usable
  `variables_reference`, so only variables from `get_variables` can be walked
- **Watch Expressions** - Continuous monitoring of expression values

**Not Implemented Yet:**
- Multi-session support (debug multiple processes simultaneously)
- Hot reload support (modify code while debugging)
- Data breakpoints (break when memory changes)

Conditional and hit-count breakpoints are implemented — see `set_breakpoint`.

## Quick Start Example

### Example 1: Learning About Debuggers

```
User: "How do .NET debuggers handle expression evaluation?"

Claude: [Uses search_debugging_concepts("expression evaluation")]

Claude: "Expression evaluation in .NET debuggers is a two-phase process:
1. Compilation Phase: Roslyn parses and compiles the expression
2. Interpretation Phase: ICorDebugEval executes it in the target process context
..."
```

### Example 2: Finding a .NET Process

```
User: "What .NET processes are running?"

Claude: [Uses list_dotnet_processes()]

Claude: "I found 3 .NET processes:
- Process 12345: MyApp.exe
- Process 12346: testhost
- Process 12347: dotnet
Would you like to attach to one of them?"
```

### Example 3: Complete Debugging Workflow

```
User: "Debug my application and find why it crashes"

Claude: [Uses list_dotnet_processes()]
"I found your application (PID 12345). Let me attach to it."

Claude: [Uses attach_to_process(12345)]
"Successfully attached. Now let me set a breakpoint at the suspected line."

Claude: [Uses set_breakpoint("/path/to/Program.cs", 42)]
"Breakpoint set at Program.cs:42. The application will pause there."

Claude: [Uses continue_execution()]
"Resuming execution. The debugger will stop at the breakpoint."

Claude: [Uses wait_for_stop()]
"Stopped at Program.cs:42 on thread 1."

Claude: [Uses get_threads()]
"Found 3 threads. Examining the main thread..."

Claude: [Uses get_stack_trace(thread_id: 1)]
"Call stack shows: Main -> ProcessUser -> ValidateEmail"

Claude: [Uses get_variables(frame_id: 0)]
"Local variables show user.Email is null, which explains the crash."

Claude: [Uses evaluate_expression("user.Name", frame_id: 0)]
"User name is 'John Doe', but email validation is failing due to null."

Claude: "Found the bug! The issue is at line 42 where ValidateEmail() doesn't
handle null email addresses. Add a null check before calling this method."
```

### Example 4: Stepping Through Code

```
User: "Step through the DoubleNumbers method to see what's happening"

Claude: [Uses get_threads()]
Claude: [Uses get_stack_trace(thread_id: 1)]
"You're currently at DoubleNumbers line 65."

Claude: [Uses step_into(thread_id: 1)]
"Stepped into MultiplyByTwo method."

Claude: [Uses get_variables(frame_id: 0)]
"The 'value' parameter is 3."

Claude: [Uses step_over(thread_id: 1)]
"Executed multiplication, returned 6."

Claude: [Uses step_out(thread_id: 1)]
"Stepped out of MultiplyByTwo, back in DoubleNumbers."
```

## Architecture

```
SharpDbg.MCP/
├── src/
│   └── SharpDbg.MCP/
│       ├── Program.cs                   # MCP server entry point
│       ├── Tools/
│       │   ├── McpTools.cs             # Documentation search tools
│       │   ├── DebuggingTools.cs       # Interactive debugging tools
│       │   └── InputValidation.cs      # Parameter validation
│       ├── Documentation/
│       │   ├── DocumentationLoader.cs  # Markdown parsing and indexing
│       │   ├── ConceptIndex.cs         # Concept categorization
│       │   └── FlowDiagramProvider.cs  # Debugging flow extraction
│       ├── Debugging/
│       │   ├── DebugSession.cs         # Wraps ManagedDebugger
│       │   ├── DebugSessionManager.cs  # Multi-session management
│       │   └── ProcessDiscovery.cs     # .NET process detection
│       ├── Logging/
│       │   └── McpLogger.cs            # Centralized structured logging
│       ├── Configuration/
│       │   └── ServerConfiguration.cs  # Environment-based configuration
│       └── Data/
│           └── how_dotnet_debuggers_work.md  # Embedded documentation
├── tests/
│   └── SharpDbg.MCP.Tests/
│       ├── Configuration/
│       │   └── ServerConfigurationTests.cs
│       └── Tools/
│           └── InputValidationTests.cs
├── examples/                    # Extension examples and templates
├── scripts/                     # Developer helper scripts
├── .github/workflows/           # CI/CD automation
├── README.md
├── CONTRIBUTING.md
└── LICENSE
```

## Development

### Running Tests

```bash
dotnet test tests/SharpDbg.MCP.Tests/SharpDbg.MCP.Tests.csproj
```

### Building from Source

```bash
dotnet build src/SharpDbg.MCP/SharpDbg.MCP.csproj
```

### Running Standalone

```bash
dotnet run --project src/SharpDbg.MCP/SharpDbg.MCP.csproj
# Server runs in stdio mode, communicating via JSON-RPC
```

## Troubleshooting

### Error responses

Every tool reports a failure the same way:

```json
{
  "success": false,
  "error": "Handle has been disposed. (0x80131C01)",
  "explanation": "The debugger is holding a handle it has already released, which an earlier evaluation ... This never recovers on retry: the debuggee stays suspended until detach_from_process releases it, after which you can attach again."
}
```

`error` is whatever the debugger said, kept verbatim. `explanation` says what the failure means and
what to do about it, for the `CORDBG_E_*` results the debugger raises — a process that has exited,
an operation that needs the debuggee stopped, a variable that is not live at this instruction, a
frame id from an earlier stop, another debugger already attached. It is `null` for failures that are
not the debugger's, such as invalid arguments.

### Server Not Appearing in Claude Desktop

1. **Check configuration path** - Verify the path in `claude_desktop_config.json` is absolute and correct
2. **Restart Claude Desktop** - Completely quit and restart the application
3. **Check Claude Desktop logs:**
   - **macOS**: `~/Library/Logs/Claude/`
   - **Windows**: `%APPDATA%\Claude\logs\`
4. **Verify .NET 10 SDK** - Ensure .NET 10 is installed: `dotnet --version`
5. **Test server manually:**
   ```bash
   cd /path/to/SharpDbg.MCP
   dotnet run
   ```
   If it starts and shows "SharpDbg MCP Server starting...", the configuration is correct.

### Viewing Server Logs

The server logs to stderr, which Claude Desktop captures. To view logs:

**macOS/Linux:**
```bash
# Run server manually to see logs
dotnet run --project /path/to/SharpDbg.MCP/SharpDbg.MCP.csproj
```

**Enable verbose logging in Claude Desktop config:**
```json
"env": {
  "SHARPDBG_LOG_LEVEL": "Debug"
}
```

**Expected log output on successful start:**
```
info: SharpDbg.MCP[0]
      SharpDbg MCP Server v1.0.0 starting...
info: SharpDbg.MCP[0]
      Configuration:
info: SharpDbg.MCP[0]
        Log Level: Information
info: SharpDbg.MCP[0]
        Max Sessions: 1
info: SharpDbg.MCP[0]
        Operation Timeout: 30s
```

### "Tools not initialized" Error

**Cause**: The server failed to initialize tools on startup.

**Solution:**
1. Check stderr logs for initialization errors
2. Verify all dependencies are present:
   ```bash
   dotnet restore
   dotnet build
   ```
3. Restart Claude Desktop

### Process Attachment Fails

#### Common Issues and Solutions

**1. Permission Denied**
```
Error: "Process {PID} is not a .NET process or cannot be accessed"
```

**Solutions:**
- On macOS/Linux, the target process may need to allow debugging
- Try running the target application with debug permissions
- Check if the process is owned by the same user

**2. Process Not .NET**
```
Error: "Process {PID} is not a .NET process"
```

**Solutions:**
- Use `list_dotnet_processes` to see which processes are detectable
- Ensure the target application is running with .NET 10 or compatible runtime
- Check if CoreCLR is loaded: Process must have `coreclr.dll` (Windows) or `libcoreclr.so/dylib` (Unix) loaded

**3. Process Exited**
```
Error: "Not attached to a process. Use attach_to_process first."
```

**Solutions:**
- Verify the process is still running: `ps -p {PID}` (Unix) or Task Manager (Windows)
- The target application may have crashed or exited normally
- Check application logs for exit reason

**4. Already Attached**
```
Error: "Already attached to a process"
```

**Solutions:**
- Detach first using `detach_from_process`
- Or check current session status with `get_process_status`

### Breakpoint Not Hit

**Symptoms**: Breakpoint shows verified=false or execution doesn't stop

**Causes & Solutions:**

1. **Source file path mismatch**
   - Use absolute paths: `/full/path/to/Program.cs` not `./Program.cs`
   - Verify path matches PDB debug information
   - Check file path in `get_stack_trace` output for correct format

2. **Code not yet loaded**
   - Some code isn't JIT-compiled until first execution
   - Set breakpoint, then trigger the code path
   - Breakpoint will verify when code loads

3. **Optimized code**
   - Ensure application is built in Debug configuration
   - Check: `dotnet build -c Debug`
   - Optimized code may inline or skip lines

### Expression Evaluation Fails

**Symptoms**: `evaluate_expression` returns error or timeout

**Common Issues:**

1. **Complex expressions not supported**
   ```
   Error: "Expression evaluation timed out"
   ```
   - Limit expressions to simple member access: `user.Name`
   - Avoid method calls (except `ToString()`)
   - No object creation or assignment

2. **Invalid frame ID**
   ```
   Error: "Frame ID must be positive"
   ```
   - Get valid frame ID from `get_stack_trace` first
   - Frame IDs are temporary and may change

3. **Evaluation timeout**
   - Increase timeout in configuration:
     ```json
     "env": {
       "SHARPDBG_EVAL_TIMEOUT_MS": "10000"
     }
     ```

### macOS/Linux: Cannot Find Processes

**Symptom**: `list_dotnet_processes` returns empty or incomplete list

**Causes:**
- Process module enumeration requires permissions on macOS/Linux
- The server falls back to process name detection

**Solutions:**
1. Run your .NET application with a distinctive name
2. Grant permissions if prompted
3. As a workaround, get PID manually:
   ```bash
   ps aux | grep dotnet
   ```
   Then use `attach_to_process` with the specific PID

### High CPU Usage

**Symptom**: Server consumes significant CPU

**Solutions:**
1. Reduce log level:
   ```json
   "env": {
     "SHARPDBG_LOG_LEVEL": "Warning"
   }
   ```
2. Check for debugger event loops
3. Ensure process is paused when not actively debugging

### Debugging Information

To gather diagnostic information for bug reports:

```json
"env": {
  "SHARPDBG_LOG_LEVEL": "Trace",
  "SHARPDBG_ENABLE_DIAGNOSTICS": "true"
}
```

Then reproduce the issue and include:
- Stderr output from server
- Claude Desktop logs
- Steps to reproduce
- Target application details (.NET version, platform)

## Technical Details

### Embedded Documentation

The server includes 1,494 lines of comprehensive documentation covering:
- ICorDebug API architecture
- Debug Adapter Protocol (DAP) implementation
- Expression evaluation (Roslyn + ICorDebugEval)
- Debugger attributes ([DebuggerDisplay], [DebuggerTypeProxy])
- Complete debugging workflows
- Comparison with other debuggers (netcoredbg)

This documentation is based on deep analysis of the SharpDbg debugger implementation and provides insights into .NET debugging internals.

### MCP Protocol Implementation

- **Transport**: stdio (standard input/output)
- **SDK**: ModelContextProtocol 0.5.0-preview.1
- **Tool Discovery**: Attribute-based (`[McpServerToolType]`, `[McpServerTool]`)
- **Logging**: stderr (doesn't interfere with stdio protocol)

### Cross-Platform Support

The server is designed to work on:
- **Windows**: Full support
- **macOS**: Supported with permission limitations for process enumeration
- **Linux**: Supported with permission limitations

## Contributing

This project is under active development. Contributions are welcome!

### Current Priorities

1. Implement core debugging tools (breakpoints, stepping, variable inspection)
2. Add comprehensive test coverage
3. Improve error handling and user feedback
4. Expand documentation

### Development Setup

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Submit a pull request

## Related Projects

- [SharpDbg](https://github.com/MattParkerDev/sharpdbg) - The underlying .NET debugger
- [Model Context Protocol](https://github.com/modelcontextprotocol) - MCP specification and SDKs
- [ClrDebug](https://github.com/lordmilko/ClrDebug) - ICorDebug API wrapper

## License

[Check main repository for license information]

## Roadmap

### Phase 1: Documentation Server ✅ COMPLETE
- [x] Search debugging concepts
- [x] Explain ICorDebug interfaces
- [x] Provide debugging flows
- [x] List concept catalog

### Phase 2: Core Debugging ✅ COMPLETE
- [x] List .NET processes
- [x] Attach to process
- [x] Get process status
- [x] Detach from process
- [x] Set breakpoints
- [x] Get threads
- [x] Get stack traces
- [x] Inspect variables
- [x] Expression evaluation
- [x] Execution control (continue/pause/step over/into/out)

### Phase 3: Advanced Features (Planned)
- [ ] Conditional breakpoints
- [ ] Exception breakpoints
- [ ] Watch expressions
- [ ] Multi-session support
- [ ] Event notifications via MCP
- [ ] Source code mapping

### Phase 4: AI-Native Features (Future)
- [ ] AI code analysis integration
- [ ] Smart debugging workflows
- [ ] Learning mode with explanations
- [ ] Bug pattern recognition

## Support

- **Issues**: Report bugs and feature requests in the issue tracker
- **Discussions**: Join conversations about MCP and .NET debugging
- **Documentation**: Check the embedded documentation via MCP tools

---

Built with ❤️ using [SharpDbg](https://github.com/MattParkerDev/sharpdbg) and the [Model Context Protocol](https://github.com/modelcontextprotocol)
