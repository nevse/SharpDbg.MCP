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
cd /path/to/SharpDbg.MCP
dotnet build src/SharpDbg.MCP/SharpDbg.MCP.csproj
```

### 2. Configure Claude Desktop

Add to your Claude Desktop configuration file:

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

### 3. Restart Claude Desktop

The SharpDbg MCP server will now be available in your conversations.

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
| `SHARPDBG_MAX_SESSIONS` | Maximum concurrent debug sessions (future) | `1` | Positive integer |
| `SHARPDBG_OPERATION_TIMEOUT_SECONDS` | Timeout for debugging operations | `30` | Positive integer |
| `SHARPDBG_ALLOW_OTHER_USER_PROCESSES` | Allow attaching to processes owned by other users | `false` | `true`, `false` |
| `SHARPDBG_EVAL_TIMEOUT_MS` | Expression evaluation timeout in milliseconds | `5000` | ≥ 100 |
| `SHARPDBG_ENABLE_DIAGNOSTICS` | Enable detailed diagnostic logging | `false` | `true`, `false` |

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

#### `list_dotnet_processes`
List all .NET processes currently running on the system.

**Parameters:** None

**Returns:** JSON array of processes with ID, name, and main module path.

#### `attach_to_process`
Attach the debugger to a .NET process.

**Parameters:**
- `process_id` (int): Process ID to attach to

**Returns:** Success/error response with session information.

#### `get_process_status`
Check the status of the current debug session.

**Parameters:** None

**Returns:** Session state including attachment status, process ID, and execution state.

#### `detach_from_process`
Detach the debugger from the current process.

**Parameters:** None

**Returns:** Success/error response.

#### `set_breakpoint`
Set a breakpoint at a specific file and line number.

**Parameters:**
- `file_path` (string): Absolute path to source file
- `line` (int): Line number (1-based)

**Returns:** Breakpoint information including ID, verification status, and message.

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

### Limitations & Planned Features

**Current Limitations (Require Upstream SharpDbg.Infrastructure Changes):**
- **Conditional Breakpoints** - Breaking only when an expression evaluates to true
- **Exception Breakpoint Filters** - Configuring break-on-exception behavior (all exceptions, unhandled only, specific types)
- **Watch Expressions** - Continuous monitoring of expression values

**Future Enhancements:**
- Multi-session support (debug multiple processes simultaneously)
- Hot reload support (modify code while debugging)
- Data breakpoints (break when memory changes)
- Function breakpoints (break on function entry)

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

[Breakpoint hits]

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
Error: "Not attached to a process. Use AttachToProcess first."
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
