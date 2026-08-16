<!-- mcp-name: io.github.nevse/dotnet-debugger-mcp -->

# .NET Debugger for MCP

An MCP server that gives an AI agent a real .NET debugger. It attaches to a running process or
launches a program under the debugger, sets line and function breakpoints, steps, reads locals and
the call stack, and evaluates expressions in the target — the same operations a developer performs
in an IDE, exposed as tools an agent can call.

Built on [SharpDbg](https://github.com/MattParkerDev/sharpdbg), driven through its supported debug
adapter surface.

## Requirements

The .NET 10 SDK. It provides `dnx`, and the server itself targets `net10.0`.

## Install

**Claude Code:**

```bash
claude mcp add dotnet-debugger -- dotnet tool exec DotnetDebugger.Mcp --yes
```

**VS Code, Claude Desktop, or any client taking JSON:**

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

There is no path to fill in and nothing to clone. The package carries the native debugger shim for
every platform, so the same configuration works on Windows, macOS and Linux.

`dnx DotnetDebugger.Mcp --yes` is the equivalent shorter form. Prefer `dotnet tool exec` in a client
launched from a desktop environment: `dnx` lives in the SDK directory, which such a client often does
not have on its `PATH`, while `dotnet` reliably is.

To pin a version and skip the resolution step, install it once and call the command directly:

```bash
dotnet tool install -g DotnetDebugger.Mcp
```

This needs `~/.dotnet/tools` on your `PATH`, which is the reason it is not the default suggestion.

## What it exposes

Sessions and processes: `list_dotnet_processes`, `attach_to_process`, `launch_program`,
`start_program`, `get_program_output`, `detach_from_process`, `list_sessions`, `close_session`.

Breakpoints: `set_breakpoint`, `set_function_breakpoint`, `remove_breakpoint`, `list_breakpoints`,
`set_exception_break_mode`.

Execution and inspection: `continue_execution`, `pause_execution`, `step_over`, `step_into`,
`step_out`, `wait_for_stop`, `get_process_status`, `get_stack_trace`, `get_threads`, `get_variables`,
`expand_variable`, `evaluate_expression`, `get_exception_info`.

It also answers questions about .NET debugging internals: `search_debugging_concepts`,
`explain_icordebug_interface`, `get_debugging_flow`, `list_debugging_concepts`.

## Configuration

Every setting is optional and read from the environment — timeouts, session limits, Just My Code,
diagnostics. They are documented in the
[full README](https://github.com/nevse/dotnet-debugger-mcp#configuration).

## Origin

A fork of [decriptor/SharpDbg.MCP](https://github.com/decriptor/SharpDbg.MCP), rewritten far enough
to warrant its own name: the debugger layer now runs over the supported DAP surface rather than
SharpDbg's internal API, and launching a program under the debugger is new.

MIT licensed. Issues and full documentation:
[github.com/nevse/dotnet-debugger-mcp](https://github.com/nevse/dotnet-debugger-mcp).
