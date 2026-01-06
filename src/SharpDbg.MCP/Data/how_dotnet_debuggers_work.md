# How .NET Debuggers Work: A Deep Dive Through SharpDbg

**Based on a comprehensive study of the SharpDbg codebase**
*SharpDbg: An open-source, cross-platform .NET debugger written in C#*
*Repository: https://github.com/MattParkerDev/sharpdbg*

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [The Foundation: ICorDebug API](#the-foundation-icordebug-api)
3. [Architecture Overview](#architecture-overview)
4. [Core Debugging Concepts](#core-debugging-concepts)
5. [Expression Evaluation Deep Dive](#expression-evaluation-deep-dive)
6. [Debugger Attributes Support](#debugger-attributes-support)
7. [Debug Adapter Protocol (DAP)](#debug-adapter-protocol-dap)
8. [Complete Debugging Flows](#complete-debugging-flows)
9. [Key Design Patterns](#key-design-patterns)
10. [Comparison: SharpDbg vs netcoredbg](#comparison-sharpdbg-vs-netcoredbg)
11. [Building Your Own Debugger](#building-your-own-debugger)

---

## Executive Summary

.NET debugging is built on a multi-layered architecture that bridges high-level IDE interactions with low-level process control. This document explores how modern .NET debuggers work by analyzing SharpDbg, a production-quality debugger implementing the Debug Adapter Protocol (DAP).

**Key Takeaways:**
- Debuggers use Microsoft's **ICorDebug COM API** for low-level process control
- The **Debug Adapter Protocol (DAP)** standardizes IDE-debugger communication
- **Expression evaluation** requires a C# compiler (Roslyn) and interpreter (ICorDebugEval)
- **Debugger attributes** like `[DebuggerDisplay]` work through runtime expression evaluation
- Modern debuggers use **event-driven** architectures, reacting to callbacks rather than polling

---

## The Foundation: ICorDebug API

### What is ICorDebug?

ICorDebug is Microsoft's low-level **Component Object Model (COM)** interface for debugging managed .NET code. It's the foundation that all .NET debuggers must use, including Visual Studio, Rider, and SharpDbg.

### Platform-Specific Libraries

The ICorDebug implementation is distributed as platform-specific native libraries:

- **Windows:** `dbgshim.dll`
- **Linux:** `libdbgshim.so`
- **macOS:** `libdbgshim.dylib`

These libraries are typically found in the .NET runtime installation, within a `runtimes/{rid}/native/` directory structure.

### Core ICorDebug Interfaces

```
ICorDebug                    - Root debugging interface, entry point
├── ICorDebugProcess         - Represents the debugged process
│   ├── ICorDebugThread      - Represents threads in the target
│   │   └── ICorDebugFrame   - Stack frames for call stack inspection
│   ├── ICorDebugModule      - Loaded assemblies/modules
│   └── ICorDebugValue       - Runtime values (locals, fields, eval results)
└── ICorDebugEval            - Expression evaluation in target context
```

### The ClrDebug Wrapper

Since ICorDebug is a COM API (native, unmanaged), C# debuggers typically use the **ClrDebug** library:
- Repository: https://github.com/lordmilko/ClrDebug
- Provides managed C# wrappers around ICorDebug interfaces
- Handles COM interop complexity, memory management
- Type-safe access to debugging primitives

**Example: Attaching to a Process**

```csharp
// 1. Locate dbgshim library for current platform
var dbgShimPath = DbgShimResolver.Resolve();
var dbgshim = new DbgShim(NativeLibrary.Load(dbgShimPath));

// 2. Initialize ICorDebug
_corDebug = ClrDebugExtensions.Automatic(dbgshim, processId);
_corDebug.Initialize();
_corDebug.SetManagedHandler(_callbacks); // Register for events

// 3. Attach to the process
_process = _corDebug.DebugActiveProcess(processId, win32Attach: false);
```

### Event-Driven Model

ICorDebug uses **callbacks** to notify the debugger of events:

```csharp
public class CorDebugManagedCallback
{
    public event Action OnCreateProcess;
    public event Action OnExitProcess;
    public event Action OnCreateThread;
    public event Action OnBreakpoint;
    public event Action OnStepComplete;
    public event Action OnException;
    public event Action OnLoadModule;
    // ... more events
}
```

The debugger **reacts** to these events rather than polling. This is critical for performance.

---

## Architecture Overview

SharpDbg demonstrates a clean three-layer architecture that separates concerns effectively:

```
┌─────────────────────────────────────────────────────────────────┐
│                        VS Code (or other DAP client)            │
│                        Debugging UI, User Interactions          │
└───────────────────────────────┬─────────────────────────────────┘
                                │ DAP Protocol (JSON-RPC)
                                │ stdin/stdout or TCP
┌───────────────────────────────▼─────────────────────────────────┐
│  SharpDbg.Cli (Entry Point)                                     │
│  • Command-line argument parsing                                │
│  • stdio/TCP stream setup                                       │
│  • Logging configuration                                        │
│  116 lines of code                                              │
└───────────────────────────────┬─────────────────────────────────┘
                                │
┌───────────────────────────────▼─────────────────────────────────┐
│  SharpDbg.Application (Protocol Layer)                          │
│  • DebugAdapter: Implements DAP protocol                        │
│  • Translates DAP requests → debugger operations                │
│  • Translates debugger events → DAP events                      │
│  • Coordinate conversion (0-based vs 1-based)                   │
│  2 C# files                                                     │
└───────────────────────────────┬─────────────────────────────────┘
                                │
┌───────────────────────────────▼─────────────────────────────────┐
│  SharpDbg.Infrastructure (Core Engine)                          │
│  • ManagedDebugger: Main debugging engine                       │
│  • BreakpointManager: Breakpoint tracking & resolution          │
│  • VariableManager: Variable lifetime & references              │
│  • SymbolReader: PDB file parsing for source mapping            │
│  • ExpressionEvaluator: C# expression compilation & execution   │
│  • AsyncStepper: Stepping through async methods                 │
│  43 C# files, wraps ClrDebug (ICorDebug)                        │
└─────────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

**CLI Layer:**
- Minimal glue code
- Sets up stdio/TCP communication
- Creates and initializes the DebugAdapter
- Optional logging to file for troubleshooting

**Application Layer:**
- **Thin translation layer** - no debugging logic
- Implements `DebugAdapterBase` from Microsoft's DAP library
- Maps DAP requests to debugger operations
- Converts debugger events to DAP protocol events
- Handles coordinate system differences (lines/columns)

**Infrastructure Layer:**
- **Heavy lifting** - all actual debugging logic
- Manages ICorDebug API interactions
- Implements breakpoint binding with lazy resolution
- Expression parsing (Roslyn) and evaluation (ICorDebugEval)
- Symbol file (PDB) reading for source-to-IL mapping
- Thread, module, and variable lifetime management

---

## Core Debugging Concepts

### 1. Breakpoints

Breakpoints go through a **lifecycle** from user request to actual code interception:

#### Lifecycle Stages

```
1. CREATE (Unverified)
   User sets breakpoint at MyFile.cs:42
   ├→ BreakpointManager.CreateBreakpoint(filePath, line)
   └→ Stored with Verified=false, no symbols yet

2. BIND (Module Load Event)
   Assembly with MyFile.cs loads
   ├→ SymbolReader reads PDB file
   ├→ Resolves line 42 → method token + IL offset
   ├→ ilCode.CreateBreakpoint(ilOffset)
   └→ Verified=true, ready to trigger

3. HIT (Execution)
   Target process executes IL at breakpoint offset
   ├→ ICorDebug fires OnBreakpoint callback
   ├→ Lookup BreakpointInfo by CorBreakpoint reference
   └→ Fire OnStopped event → UI updates

4. REMOVE
   User deletes breakpoint or changes file
   ├→ CorBreakpoint.Deactivate()
   └→ Remove from BreakpointManager
```

#### Why Lazy Binding?

Breakpoints start **unverified** because:
- Symbols (PDB files) may not be loaded yet
- The module containing the code might load dynamically later
- Users can set breakpoints before launching the program

When a module loads, `TryBindPendingBreakpoints()` attempts to resolve all unverified breakpoints against the new symbols.

#### Source Line to IL Offset Resolution

```csharp
// From SymbolReader
public ResolvedBreakpoint? ResolveBreakpoint(string filePath, int line)
{
    // 1. Find which method contains this source line
    var method = FindMethodContainingLine(filePath, line);

    // 2. Map source line → IL offset using PDB sequence points
    var ilOffset = GetILOffsetForSourceLine(method, line);

    // 3. Return method token + IL offset
    return new ResolvedBreakpoint(
        methodToken: method.Token,
        ilOffset: ilOffset,
        startLine: actualStartLine,
        endLine: actualEndLine
    );
}
```

**Sequence points** in PDB files map IL offsets to source locations:
```
IL_0000: (15, 9) - (15, 10)   // Line 15, column 9-10
IL_0001: (16, 13) - (16, 38)  // Line 16, column 13-38
IL_000A: (17, 13) - (17, 31)  // Line 17, column 13-31
```

### 2. Stepping

Stepping allows users to advance execution one line/instruction at a time.

#### Step Types

**Step Over (Next):**
- Execute current line, stop at next line in same method
- If current line calls a method, execute it fully

**Step In:**
- Execute current line, stopping at first line of any called method
- Allows diving into function calls

**Step Out:**
- Complete current method, stop in the caller

#### Implementation with ICorDebugStepper

```csharp
public void StepOver(CorDebugThread thread)
{
    var frame = thread.ActiveFrame;
    var ilFrame = (CorDebugILFrame)frame;

    // Create a stepper for this frame
    CorDebugStepper stepper = frame.CreateStepper();

    // Configure what to step over/into
    stepper.SetInterceptMask(
        CorDebugIntercept.INTERCEPT_ALL &
        ~CorDebugIntercept.INTERCEPT_SECURITY
    );

    // Get current IL offset
    var currentOffset = ilFrame.IP.pnOffset;

    // Find sequence point range for current statement
    var (startOffset, endOffset) = symbolReader
        .GetSequencePointRange(currentOffset);

    // Step within this range, don't step into calls
    var stepRange = new COR_DEBUG_STEP_RANGE {
        startOffset = startOffset,
        endOffset = endOffset
    };
    stepper.StepRange(stepIn: false, [stepRange], 1);

    // Continue execution - will stop when step completes
    _process.Continue(false);
}
```

#### Async Method Stepping Challenge

Stepping through async methods is **complex** because:
- Async methods are rewritten by the compiler into state machines
- The "next line" might be in a continuation callback
- The actual call stack involves compiler-generated types

SharpDbg includes an `AsyncStepper` class to handle this, but it's listed as a current limitation.

### 3. Variable Inspection

Variables are exposed through a **reference-based** system:

```
Scopes (variablesReference: 1001)
├── Locals (variablesReference: 1002)
│   ├── myInt = 42 (no reference, primitive)
│   └── myObject = {...} (variablesReference: 1003)
│       ├── Name = "test" (variablesReference: 1004)
│       └── Count = 5 (no reference, primitive)
└── Arguments (variablesReference: 1005)
    └── param1 = {...}
```

**Why references?**
- **Lazy loading**: Don't expand all objects immediately
- **Circular references**: Prevent infinite loops
- **Performance**: Only fetch what the user expands in UI

#### Variable Value Extraction

```csharp
private CorDebugValueValueResult GetValueForCorDebugValue(CorDebugValue value)
{
    return value switch
    {
        // Primitives: int, bool, double, etc.
        CorDebugGenericValue generic => GetPrimitiveValue(generic),

        // Strings: special handling
        CorDebugStringValue str => new(
            "string",
            $"\"{str.GetString()}\"",
            requiresEval: false,
            proxyType: null
        ),

        // Arrays
        CorDebugArrayValue array => new(
            GetArrayTypeName(array),
            GetArrayDisplay(array),  // e.g., "int[5]"
            requiresEval: false,
            proxyType: null
        ),

        // Objects: Check for debugger attributes
        CorDebugObjectValue obj => GetObjectValue(obj),

        _ => throw new NotImplementedException()
    };
}
```

---

## Expression Evaluation Deep Dive

Expression evaluation is what allows users to type `myList.Count` or `customer.Name.ToUpper()` in the Watch window and see results. This is **one of the most complex parts** of a debugger.

### The Challenge

When the debugger is paused:
- The target process is frozen at a specific point
- We need to **evaluate C# expressions in the target's context**
- We need access to local variables, parameters, fields, etc.
- We can't just compile and run code in the debugger process

### SharpDbg's Two-Phase Approach

```
┌─────────────────────────────────────────────────────────────┐
│  Phase 1: COMPILE (Roslyn-based, in debugger process)      │
│                                                             │
│  "myList.Count + 10"                                        │
│         ↓                                                   │
│  Roslyn Parser (CSharpSyntaxTree.ParseText)                │
│         ↓                                                   │
│  Syntax Tree (AST)                                          │
│         ↓                                                   │
│  ExpressionSyntaxVisitor walks tree                         │
│         ↓                                                   │
│  Generate CommandBase[] instructions (custom bytecode)      │
│         ↓                                                   │
│  CompiledExpression                                         │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  Phase 2: INTERPRET (ICorDebugEval, in target process)     │
│                                                             │
│  CompiledExpressionInterpreter.Interpret()                  │
│         ↓                                                   │
│  Process each CommandBase instruction                       │
│         ↓                                                   │
│  Use ICorDebugEval to execute operations:                   │
│    • Load variable/field values                             │
│    • Call methods                                           │
│    • Perform operators (+, -, ==, etc.)                     │
│    • Create new objects                                     │
│         ↓                                                   │
│  Result as ICorDebugValue                                   │
│         ↓                                                   │
│  Format for display                                         │
└─────────────────────────────────────────────────────────────┘
```

### Why This Two-Phase Design?

**Alternative 1: Interpret C# directly**
- ❌ C# parsing is extremely complex
- ❌ Reinventing the wheel

**Alternative 2: Compile to IL and execute**
- ❌ Requires full Roslyn compilation (slow, heavy)
- ❌ Need to load compiled assembly into target
- ❌ Complex setup and teardown

**SharpDbg's Approach:**
- ✅ Use Roslyn just for **parsing** (fast, reliable)
- ✅ Generate custom instructions (like bytecode)
- ✅ Custom interpreter with ICorDebugEval (efficient)
- ✅ Full control over evaluation semantics

### Phase 1: Compilation Example

**Input Expression:**
```csharp
customer.Name.Length + 10
```

**Generated Instructions (conceptual):**
```csharp
[
    LoadLocal("customer"),           // Push customer onto eval stack
    AccessMember("Name"),            // Pop customer, push customer.Name
    AccessMember("Length"),          // Pop Name, push Name.Length
    LoadConstant(10),                // Push 10
    BinaryOperation(Add),            // Pop 2 values, push sum
    Return                           // Result is top of stack
]
```

### Phase 2: Interpretation Example

```csharp
public async Task<CorDebugValue> Interpret(
    CompiledExpression expression,
    CompiledExpressionEvaluationContext context)
{
    var evalStack = new Stack<CorDebugValue>();

    foreach (var instruction in expression.Instructions)
    {
        switch (instruction)
        {
            case LoadLocalCommand load:
                // Use ICorDebugILFrame.GetLocalVariable()
                var value = context.Frame.GetLocalVariable(load.Index);
                evalStack.Push(value);
                break;

            case AccessMemberCommand access:
                var obj = evalStack.Pop();

                // Use ICorDebugEval to access field/property
                var field = obj.GetFieldValue(access.MemberToken);
                evalStack.Push(field);
                break;

            case BinaryOperationCommand binOp:
                var right = evalStack.Pop();
                var left = evalStack.Pop();

                // Use ICorDebugEval to perform operation
                var eval = context.Thread.CreateEval();
                var result = await PerformBinaryOp(eval, left, right, binOp.Op);
                evalStack.Push(result);
                break;
        }
    }

    return evalStack.Pop(); // Final result
}
```

### ICorDebugEval: The Magic

`ICorDebugEval` is how we execute code **in the target process**:

```csharp
// Call a method in the target
var eval = thread.CreateEval();
eval.CallFunction(
    function: methodInfo,
    args: argumentValues
);

// Must continue the process for eval to execute
process.Continue(false);

// Wait for EvalComplete event
await WaitForEvalComplete();

// Get result
var result = eval.GetResult();
```

**Important:** ICorDebugEval operations are **asynchronous**:
1. Set up the evaluation
2. Continue the target process
3. Target executes the operation
4. ICorDebug fires `EvalComplete` callback
5. Retrieve result

This is why `HandleEvaluateRequestAsync` is async in DebugAdapter.

---

## Debugger Attributes Support

.NET provides several attributes that customize how objects appear in debuggers:
- `[DebuggerDisplay]`
- `[DebuggerTypeProxy]`
- `[DebuggerBrowsable]`

### [DebuggerDisplay] - Custom String Representation

**Example:**
```csharp
[DebuggerDisplay("Count = {Count}")]
public class MyList<T>
{
    private T[] _items;
    public int Count => _items.Length;
}
```

**In Debugger:**
```
myList = Count = 5  // Instead of "MyList<int>"
```

**How It Works:**

1. **Attribute Discovery:**
```csharp
var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
var hasAttribute = metaDataImport.TryGetCustomAttributeByName(
    typeToken,
    "System.Diagnostics.DebuggerDisplayAttribute",
    out var attributeBlob
);
```

2. **Extract Format String:**
```csharp
// Parse attribute constructor arguments from metadata blob
var displayFormat = ParseAttributeString(attributeBlob);
// displayFormat = "Count = {Count}"
```

3. **Treat as Interpolated String:**
```csharp
// Wrap in $"..." for evaluation
var expression = $"$\"{displayFormat}\"";
// Results in: $"Count = {Count}"

// Compile and evaluate (Phase 1 + 2)
var compiled = ExpressionCompiler.Compile(expression, isDebuggerDisplay: true);
var result = await Interpreter.Interpret(compiled, context);
```

4. **Display Result:**
```
myList = Count = 5
```

**Why SharpDbg supports this and netcoredbg doesn't:**
- Requires **full C# expression evaluation**
- netcoredbg (C++) doesn't have Roslyn integration
- SharpDbg leverages its expression evaluator infrastructure

### [DebuggerTypeProxy] - Alternative View

**Example:**
```csharp
[DebuggerTypeProxy(typeof(ListDebugView<>))]
public class List<T>
{
    private T[] _items;      // Internal implementation detail
    private int _size;       // Users don't care about this
    private int _version;    // Or this
}

internal class ListDebugView<T>
{
    private List<T> _list;

    public ListDebugView(List<T> list) => _list = list;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T[] Items => _list.ToArray();  // Show just the elements!
}
```

**Without Proxy:**
```
myList = List<int>
  ├─ _items = int[10]     // Includes unused capacity
  ├─ _size = 5
  └─ _version = 3
```

**With Proxy:**
```
myList = List<int>
  ├─ [0] = 1
  ├─ [1] = 2
  ├─ [2] = 3
  ├─ [3] = 4
  └─ [4] = 5
```

**Implementation:**

```csharp
// 1. Detect proxy attribute
var proxyTypeName = GetDebuggerTypeProxyTypeName(objectValue);

// 2. Resolve proxy type in target's assembly
var proxyType = FindType(proxyTypeName, objectValue.Module);

// 3. Create proxy instance: new ListDebugView(myList)
var proxyConstructor = proxyType.GetConstructor(objectValue.Type);
var eval = thread.CreateEval();
eval.NewObject(proxyConstructor, [objectValue]);
await WaitForEvalComplete();
var proxyInstance = eval.GetResult();

// 4. Show proxy's members instead of original object's members
return GetMembersOf(proxyInstance);
```

### [DebuggerBrowsable] - Visibility Control

```csharp
public class Person
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private int _id;  // Hidden in debugger

    public string Name { get; set; }  // Visible

    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    public Address Address { get; set; }  // Collapsed by default

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public Dictionary<string, string> Properties { get; set; }  // Show contents, not wrapper
}
```

**States:**
- `Never`: Don't show this member at all
- `Collapsed`: Show but don't auto-expand (default for complex types)
- `RootHidden`: Show the member's children as if they were this object's children

---

## Debug Adapter Protocol (DAP)

The Debug Adapter Protocol (DAP) is Microsoft's **standard protocol** for communication between debuggers and development tools.

### Why DAP Matters

**Before DAP:**
- Each IDE had custom debugger integration
- Debuggers were tightly coupled to specific IDEs
- Building a new debugger meant implementing N IDE plugins

**With DAP:**
- **One protocol** for all IDE ↔ debugger communication
- **Write once**: Implement DAP, work with any DAP client
- **Mix and match**: Any DAP debugger with any DAP client

**Ecosystem:**
- **Clients**: VS Code, Vim/Neovim, Emacs, Visual Studio, Eclipse
- **Debuggers**: SharpDbg, netcoredbg, delve (Go), lldb-vscode (C++), debugpy (Python)

### Protocol Architecture

```
┌──────────────────────────────────────────────────────┐
│  IDE/Editor (DAP Client)                             │
│  • VS Code debugger UI                               │
│  • vim-dap plugin                                    │
│  • Any DAP-compatible client                         │
└────────────────────┬─────────────────────────────────┘
                     │
                     │ JSON-RPC Messages
                     │ (stdin/stdout or TCP)
                     │
┌────────────────────▼─────────────────────────────────┐
│  Debugger (DAP Server)                               │
│  • SharpDbg.Cli                                      │
│  • netcoredbg                                        │
│  • Any DAP-compatible debugger                       │
└──────────────────────────────────────────────────────┘
```

### Message Types

**1. Requests (Client → Server)**
```json
{
  "seq": 1,
  "type": "request",
  "command": "setBreakpoints",
  "arguments": {
    "source": { "path": "/path/to/file.cs" },
    "breakpoints": [
      { "line": 42 },
      { "line": 55 }
    ]
  }
}
```

**2. Responses (Server → Client)**
```json
{
  "seq": 2,
  "type": "response",
  "request_seq": 1,
  "command": "setBreakpoints",
  "success": true,
  "body": {
    "breakpoints": [
      { "id": 1, "verified": true, "line": 42 },
      { "id": 2, "verified": false, "line": 55, "message": "No symbols" }
    ]
  }
}
```

**3. Events (Server → Client)**
```json
{
  "seq": 3,
  "type": "event",
  "event": "stopped",
  "body": {
    "reason": "breakpoint",
    "threadId": 12345,
    "allThreadsStopped": true
  }
}
```

### Common DAP Requests

| Request | Purpose |
|---------|---------|
| `initialize` | Handshake, exchange capabilities |
| `launch` | Start debugging a program |
| `attach` | Attach to running process |
| `setBreakpoints` | Set/update breakpoints for a file |
| `setExceptionBreakpoints` | Configure exception handling |
| `configurationDone` | Initialization complete, start debugging |
| `continue` | Resume execution |
| `next` | Step over |
| `stepIn` | Step into |
| `stepOut` | Step out |
| `pause` | Break execution |
| `threads` | Get list of threads |
| `stackTrace` | Get call stack for thread |
| `scopes` | Get variable scopes (locals, args, etc.) |
| `variables` | Get variables in a scope |
| `evaluate` | Evaluate expression in context |
| `disconnect` | End debugging session |

### Common DAP Events

| Event | Purpose |
|-------|---------|
| `initialized` | Debugger ready, client can set breakpoints |
| `stopped` | Execution paused (breakpoint, exception, step complete) |
| `continued` | Execution resumed |
| `exited` | Program exited |
| `terminated` | Debugging session ended |
| `thread` | Thread started or exited |
| `module` | Module/assembly loaded or unloaded |
| `breakpoint` | Breakpoint state changed (verified, etc.) |
| `output` | Debug output/console message |

### Implementation in SharpDbg

**Request Handler:**
```csharp
protected override SetBreakpointsResponse HandleSetBreakpointsRequest(
    SetBreakpointsArguments arguments)
{
    // 1. Extract file and line numbers
    var filePath = arguments.Source?.Path;
    var lines = arguments.Breakpoints?
        .Select(bp => ConvertClientLineToDebugger(bp.Line))
        .ToArray();

    // 2. Call core debugger
    var breakpoints = _debugger.SetBreakpoints(filePath, lines);

    // 3. Build DAP response
    var responseBreakpoints = breakpoints.Select(bp => new Breakpoint
    {
        Id = bp.Id,
        Verified = bp.Verified,
        Line = ConvertDebuggerLineToClient(bp.Line),
        Message = bp.Message
    }).ToList();

    return new SetBreakpointsResponse
    {
        Breakpoints = responseBreakpoints
    };
}
```

**Event Publisher:**
```csharp
// Subscribe to debugger events
_debugger.OnStopped += (threadId, reason) =>
{
    // Convert debugger event to DAP event
    Protocol.SendEvent(new StoppedEvent
    {
        Reason = ConvertStopReason(reason),
        ThreadId = threadId,
        AllThreadsStopped = true
    });
};
```

### Transport: stdin/stdout

Most DAP implementations use **stdio** for simplicity:

```csharp
// Debugger startup
var inputStream = Console.OpenStandardInput();
var outputStream = Console.OpenStandardOutput();

var adapter = new DebugAdapter();
adapter.Initialize(inputStream, outputStream);
adapter.Protocol.Run();  // Start message loop
```

**Why stdio?**
- Simple: No need to pick ports, handle conflicts
- Secure: Process isolation, no network exposure
- Standard: Works everywhere
- Clean lifecycle: Process dies = connection ends

**TCP Alternative:**
- Useful for remote debugging
- Requires port management
- SharpDbg supports it via `--server=PORT` (not fully implemented yet)

---

## Complete Debugging Flows

### Flow 1: Setting a Breakpoint

```
┌──────────────────────────────────────────────────────────────┐
│ 1. User Action                                               │
│    User clicks line 42 in VS Code editor                     │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 2. VS Code → DAP Request                                     │
│    {                                                         │
│      "command": "setBreakpoints",                            │
│      "arguments": {                                          │
│        "source": { "path": "/src/Program.cs" },              │
│        "breakpoints": [{ "line": 42 }]                       │
│      }                                                       │
│    }                                                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 3. SharpDbg.Application (DebugAdapter)                       │
│    HandleSetBreakpointsRequest()                             │
│    • Convert client line number (possibly 0-based)           │
│    • Call _debugger.SetBreakpoints()                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 4. SharpDbg.Infrastructure (ManagedDebugger)                 │
│    SetBreakpoints(filePath, lines)                           │
│    • Clear old breakpoints for this file                     │
│    • Create new BreakpointInfo for each line                 │
│    • Call TryBindBreakpoint() for each                       │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 5. BreakpointManager                                         │
│    CreateBreakpoint(filePath, line)                          │
│    • Assign unique ID                                        │
│    • Store with Verified=false (no symbols yet)              │
│    • Index by file path for quick lookup                     │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 6. Try to Bind (if symbols available)                        │
│    TryBindBreakpoint(bp)                                     │
│    • Search loaded modules for symbols                       │
│    • SymbolReader.ResolveBreakpoint(filePath, line)          │
│    • If found: methodToken + IL offset                       │
│    • ilCode.CreateBreakpoint(ilOffset)                       │
│    • bp.Verified = true                                      │
│    └─→ Or bp.Verified = false, message = "No symbols"       │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 7. Response Back to VS Code                                  │
│    SetBreakpointsResponse {                                  │
│      breakpoints: [{                                         │
│        id: 1,                                                │
│        verified: true,                                       │
│        line: 42                                              │
│      }]                                                      │
│    }                                                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 8. VS Code UI Update                                         │
│    • Show red dot next to line 42 (verified)                 │
│    • Or hollow circle (unverified)                           │
└──────────────────────────────────────────────────────────────┘
```

### Flow 2: Hitting a Breakpoint

```
┌──────────────────────────────────────────────────────────────┐
│ 1. Target Process Execution                                  │
│    Target executes IL instruction at breakpoint offset       │
│    • CPU hits breakpoint instruction (int 3 on x86)          │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 2. ICorDebug → Callback                                      │
│    OnBreakpoint(CorDebugBreakpoint corBreakpoint)            │
│    • Triggered by CLR debugging infrastructure               │
│    • Passes ICorDebugFunctionBreakpoint handle               │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 3. ManagedDebugger Event Handler                             │
│    HandleBreakpoint(corBreakpoint)                           │
│    • Lookup BreakpointInfo by corBreakpoint reference        │
│    • Get thread ID, source location from current frame       │
│    • Fire OnStopped event                                    │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 4. Event Subscription Handler                                │
│    _debugger.OnStopped += (threadId, filePath, line, reason) │
│    • Triggered by OnStopped.Invoke()                         │
│    • In DebugAdapter's SubscribeToDebuggerEvents()           │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 5. Send DAP Event                                            │
│    Protocol.SendEvent(new StoppedEvent {                     │
│      reason: "breakpoint",                                   │
│      threadId: 12345,                                        │
│      allThreadsStopped: true                                 │
│    })                                                        │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 6. VS Code Receives Event                                    │
│    • UI shows "Paused on breakpoint"                         │
│    • Highlights current line                                 │
│    • Sends follow-up requests:                               │
│      - threads (get thread list)                             │
│      - stackTrace (get call stack)                           │
│      - scopes (get variable scopes)                          │
└──────────────────────────────────────────────────────────────┘
```

### Flow 3: Evaluating an Expression

```
┌──────────────────────────────────────────────────────────────┐
│ 1. User Action                                               │
│    User types "customer.Name.Length" in Watch window         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 2. VS Code → DAP Request                                     │
│    {                                                         │
│      "command": "evaluate",                                  │
│      "arguments": {                                          │
│        "expression": "customer.Name.Length",                 │
│        "frameId": 42,                                        │
│        "context": "watch"                                    │
│      }                                                       │
│    }                                                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 3. DebugAdapter.HandleEvaluateRequestAsync()                 │
│    var result = await _debugger.Evaluate(                    │
│      expression, frameId                                     │
│    )                                                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 4. PHASE 1: Compilation (Roslyn)                             │
│    ExpressionCompiler.Compile(expression)                    │
│    ├─ Parse: "customer.Name.Length"                          │
│    ├─ Syntax Tree:                                           │
│    │    MemberAccess                                         │
│    │    ├─ MemberAccess                                      │
│    │    │  ├─ Identifier: customer                           │
│    │    │  └─ Identifier: Name                               │
│    │    └─ Identifier: Length                                │
│    └─ Generate Instructions:                                 │
│        [LoadLocal("customer"),                               │
│         AccessMember("Name"),                                │
│         AccessMember("Length"),                              │
│         Return]                                              │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 5. PHASE 2: Interpretation (ICorDebugEval)                   │
│    CompiledExpressionInterpreter.Interpret(compiled, context)│
│    ├─ Get thread and frame from frameId                      │
│    ├─ Create evaluation context:                             │
│    │    - Current thread                                     │
│    │    - Current IL frame                                   │
│    │    - Access to locals and arguments                     │
│    └─ Execute each instruction:                              │
│        1. LoadLocal("customer")                              │
│           → ilFrame.GetLocalVariable(0)                      │
│           → Push CorDebugValue on eval stack                 │
│        2. AccessMember("Name")                               │
│           → Pop customer value                               │
│           → objectValue.GetFieldValue(nameFieldToken)        │
│           → Push Name value on stack                         │
│        3. AccessMember("Length")                             │
│           → Pop Name (string) value                          │
│           → stringValue.GetFieldValue(lengthFieldToken)      │
│           → Push Length value on stack                       │
│        4. Return                                             │
│           → Pop final value: 7 (int)                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 6. Format Result                                             │
│    GetValueForCorDebugValue(result)                          │
│    • Type: "int"                                             │
│    • Value: "7"                                              │
│    • variablesReference: 0 (primitives don't have children)  │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 7. Response to VS Code                                       │
│    EvaluateResponse {                                        │
│      result: "7",                                            │
│      type: "int",                                            │
│      variablesReference: 0                                   │
│    }                                                         │
└────────────────┬─────────────────────────────────────────────┘
                 │
┌────────────────▼─────────────────────────────────────────────┐
│ 8. VS Code Display                                           │
│    Watch window:                                             │
│      customer.Name.Length = 7                                │
└──────────────────────────────────────────────────────────────┘
```

---

## Key Design Patterns

### 1. Event-Driven Architecture

Debuggers are fundamentally **reactive systems**:

```csharp
// Don't do this (polling)
while (true)
{
    if (HasBreakpointBeenHit())
        HandleBreakpoint();
    if (HasThreadStarted())
        HandleThreadStart();
    // ... check all possible events
    Thread.Sleep(10);
}

// Instead, use callbacks (event-driven)
_callbacks.OnBreakpoint += HandleBreakpoint;
_callbacks.OnThreadStarted += HandleThreadStart;
_callbacks.OnException += HandleException;
// Let ICorDebug notify us when events occur
```

### 2. Lazy Resolution

Don't try to resolve everything eagerly:

```csharp
// Breakpoints start unverified
var bp = new BreakpointInfo {
    FilePath = path,
    Line = line,
    Verified = false  // Don't know if valid yet
};

// Later, when symbols load
OnModuleLoaded += (module) => {
    TryBindPendingBreakpoints(); // Try to verify now
};
```

### 3. Reference-Based Inspection

Use handles/references instead of eagerly fetching all data:

```csharp
// Bad: Recursively fetch all object fields
public Variable GetVariable(object obj)
{
    return new Variable {
        Name = obj.Name,
        Value = obj.Value.ToString(),
        Children = obj.Fields.Select(f => GetVariable(f)).ToList()
        // ❌ Expensive! Might be circular! Might be huge!
    };
}

// Good: Use reference handles for lazy loading
public Variable GetVariable(object obj)
{
    int? reference = null;
    if (obj.HasChildren)
        reference = _variableManager.CreateReference(obj);

    return new Variable {
        Name = obj.Name,
        Value = GetValueString(obj),
        VariablesReference = reference  // UI can expand later
    };
}
```

### 4. Partial Classes for Organization

ManagedDebugger is split across multiple files:

```
ManagedDebugger.cs                         // Core class, initialization
ManagedDebugger_EventHandlers.cs           // ICorDebug callback handlers
ManagedDebugger_VariableValues.cs          // Variable value extraction
ManagedDebugger_VariableInfo.cs            // Variable metadata
ManagedDebugger_FrameSourceInfo.cs         // Stack frame source mapping
ManagedDebugger_IdentifierResolver.cs      // Expression variable lookup
```

This keeps related functionality grouped without huge single files.

### 5. Adapter Pattern

DebugAdapter is a **thin translation layer**:
- Doesn't implement debugging logic
- Just translates between protocols
- ManagedDebugger is protocol-agnostic

This allows:
- Testing ManagedDebugger without DAP
- Supporting multiple protocols (future: GDB, LLDB)
- Clear separation of concerns

---

## Comparison: SharpDbg vs netcoredbg

| Feature | SharpDbg | netcoredbg | Notes |
|---------|----------|------------|-------|
| **Language** | Pure C# | C++ | SharpDbg: easier to maintain for .NET devs |
| **ICorDebug Wrapper** | ClrDebug | Custom | ClrDebug is well-maintained, comprehensive |
| **DAP Support** | ✅ | ✅ | Both implement Debug Adapter Protocol |
| **Cross-Platform** | ✅ | ✅ | Windows, Linux, macOS |
| **Expression Evaluation** | ✅ Roslyn + custom | ✅ Custom | SharpDbg uses Roslyn for parsing |
| **[DebuggerDisplay]** | ✅ | ❌ | Requires C# expression eval |
| **[DebuggerTypeProxy]** | ✅ | ❌ | Requires runtime proxy instantiation |
| **[DebuggerBrowsable]** | ✅ | ❌ | Metadata-driven feature |
| **Async Stepping** | ⚠️ Limited | ⚠️ Limited | Both struggle with async state machines |
| **Performance** | Good | Better | C++ has lower overhead |
| **Memory Footprint** | Higher | Lower | .NET runtime + Roslyn |
| **Build Complexity** | Simple | Complex | C#: `dotnet build`, C++: CMake + dependencies |

### Why Choose SharpDbg?

**Pros:**
- **Debugger attributes work**: Better object visualization
- **Easier to extend**: C# is more approachable than C++
- **Roslyn integration**: Powerful expression parsing
- **Pure managed**: No native dependencies to manage
- **Modern C# features**: Pattern matching, LINQ, async/await

**Cons:**
- Slightly slower startup (JIT compilation)
- Higher memory usage (.NET runtime overhead)
- Larger executable (includes Roslyn)

### Why Choose netcoredbg?

**Pros:**
- Faster runtime performance
- Lower memory footprint
- Smaller executable
- Mature, battle-tested (Samsung project)

**Cons:**
- C++ is harder to maintain
- Missing debugger attribute support
- More complex build system
- Steeper learning curve for contributors

### Philosophical Difference

**SharpDbg**: "A debugger for .NET should be written in .NET"
- Leverages .NET ecosystem (Roslyn, NuGet, C# language features)
- Natural integration with .NET metadata and attributes
- Easier for .NET developers to contribute

**netcoredbg**: "A debugger should be fast and lean"
- Minimal overhead, maximum performance
- No dependency on .NET runtime for the debugger itself
- Traditional systems programming approach

---

## Building Your Own Debugger

### Prerequisites

1. **.NET SDK**: For managed debuggers (C#)
2. **ICorDebug Documentation**: https://docs.microsoft.com/en-us/dotnet/framework/unmanaged-api/debugging/
3. **ClrDebug Library**: https://github.com/lordmilko/ClrDebug
4. **DAP Specification**: https://microsoft.github.io/debug-adapter-protocol/
5. **Symbol Format Knowledge**: PDB files, sequence points, metadata

### Minimal Debugger Skeleton

```csharp
using ClrDebug;

class MinimalDebugger
{
    private CorDebug _corDebug;
    private CorDebugProcess _process;
    private CorDebugManagedCallback _callbacks;

    public void Launch(string exePath)
    {
        // 1. Initialize ICorDebug
        var dbgShimPath = FindDbgShim();
        var dbgshim = new DbgShim(NativeLibrary.Load(dbgShimPath));
        _corDebug = dbgshim.CreateDebuggingInterfaceFromVersion(
            CorDebugInterfaceVersion.CorDebugVersion_2_0
        );
        _corDebug.Initialize();

        // 2. Set up callbacks
        _callbacks = new CorDebugManagedCallback();
        _callbacks.OnCreateProcess += OnProcessCreated;
        _callbacks.OnBreakpoint += OnBreakpointHit;
        _callbacks.OnStepComplete += OnStepComplete;
        _corDebug.SetManagedHandler(_callbacks);

        // 3. Launch process
        _process = _corDebug.CreateProcess(
            exePath,
            commandLine: exePath,
            environment: null,
            currentDirectory: Path.GetDirectoryName(exePath),
            CREATE_NEW_CONSOLE
        );
    }

    private void OnProcessCreated(object sender, CreateProcessCorDebugManagedCallbackEventArgs e)
    {
        Console.WriteLine("Process created");
        // Continue execution
        e.Controller.Continue(false);
    }

    private void OnBreakpointHit(object sender, BreakpointCorDebugManagedCallbackEventArgs e)
    {
        Console.WriteLine("Breakpoint hit!");
        // Process is now stopped, can inspect state

        // Get call stack
        var thread = e.AppDomain.Process.Threads[0];
        foreach (var frame in thread.ActiveChain.Frames)
        {
            var function = frame.Function;
            Console.WriteLine($"  at {function.Class.Module.Name}!{function.Token:X}");
        }

        // Continue? Step? User decides
        e.Controller.Continue(false);
    }

    public void SetBreakpoint(string assemblyName, int methodToken, int ilOffset)
    {
        // Find module by name
        foreach (var module in _process.Modules)
        {
            if (module.Name.Contains(assemblyName))
            {
                var function = module.GetFunctionFromToken(methodToken);
                var breakpoint = function.ILCode.CreateBreakpoint(ilOffset);
                breakpoint.Activate(true);
                break;
            }
        }
    }
}
```

### Roadmap to Full Debugger

**Phase 1: Basic Process Control**
- Launch and attach to processes
- Handle process lifecycle events
- Continue/pause execution
- Basic breakpoint support (method entry)

**Phase 2: Symbol Support**
- Read PDB files (System.Reflection.Metadata)
- Map source lines to IL offsets
- Resolve source file paths
- Line-based breakpoints

**Phase 3: Variable Inspection**
- Read local variables
- Navigate object graphs (fields, properties)
- Handle primitives, strings, arrays
- Basic type display

**Phase 4: Stepping**
- Step over (next line in same method)
- Step in (into method calls)
- Step out (to caller)
- Sequence point handling

**Phase 5: Expression Evaluation**
- Parse simple expressions (field access)
- Use ICorDebugEval for property getters
- Handle method calls
- Operator support

**Phase 6: Protocol Integration**
- Implement DAP or similar protocol
- Request/response handling
- Event publishing
- Coordinate conversion (lines, columns)

**Phase 7: Advanced Features**
- Exception breakpoints
- Conditional breakpoints
- Debugger attribute support
- Async/await stepping
- Edit and continue

### Common Pitfalls

**1. Forgetting to Continue:**
```csharp
// Wrong: Process stops forever
_callbacks.OnLoadModule += (sender, e) => {
    // Handle module load
    // ❌ Forgot to continue!
};

// Right: Always continue after handling event
_callbacks.OnLoadModule += (sender, e) => {
    HandleModuleLoad(e.Module);
    e.Controller.Continue(false);  // ✅
};
```

**2. Neutered Objects:**
```csharp
// ICorDebug objects become invalid after continue
var thread = e.Thread;
e.Controller.Continue(false);
// ❌ 'thread' is now neutered, can't use it

// Solution: Extract data before continuing
var threadId = e.Thread.Id;
var frameName = e.Thread.ActiveFrame.Function.Name;
e.Controller.Continue(false);
// ✅ Now use threadId and frameName
```

**3. Assuming Synchronous Eval:**
```csharp
// ICorDebugEval is asynchronous!
var eval = thread.CreateEval();
eval.CallFunction(method, args);
var result = eval.GetResult();  // ❌ Not ready yet!

// Must continue, wait for callback, then get result
eval.CallFunction(method, args);
_process.Continue(false);
// Wait for OnEvalComplete callback
_callbacks.OnEvalComplete += (sender, e) => {
    var result = e.Eval.GetResult();  // ✅ Now it's ready
};
```

**4. Line Number Confusion:**
```csharp
// VS Code uses 0-based lines, humans use 1-based
// DAP allows client to specify convention
_clientLinesStartAt1 = arguments.LinesStartAt1 ?? true;

int ConvertClientToDebugger(int clientLine) =>
    _clientLinesStartAt1 ? clientLine : clientLine + 1;

int ConvertDebuggerToClient(int debuggerLine) =>
    _clientLinesStartAt1 ? debuggerLine : debuggerLine - 1;
```

---

## Conclusion

Modern .NET debuggers are sophisticated systems that bridge multiple abstraction layers:

1. **Low-level process control** via ICorDebug COM API
2. **Symbol and metadata** interpretation from PDB files
3. **Expression compilation** using Roslyn
4. **Runtime evaluation** via ICorDebugEval
5. **Protocol translation** for IDE communication
6. **Event-driven architecture** for responsive debugging

SharpDbg demonstrates that it's possible to build a production-quality debugger entirely in C#, leveraging the .NET ecosystem for parsing, metadata access, and modern language features. This approach trades some performance for significant gains in maintainability, extensibility, and developer experience.

Whether you're building your own debugging tools, contributing to existing projects, or just trying to understand how the magic works when you press F5, this knowledge provides the foundation for working with .NET's debugging infrastructure.

---

## Further Reading

**Official Documentation:**
- [ICorDebug API Reference](https://docs.microsoft.com/en-us/dotnet/framework/unmanaged-api/debugging/)
- [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/)
- [.NET Debugging Architecture](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/dac-notes.md)

**Projects:**
- [SharpDbg](https://github.com/MattParkerDev/sharpdbg) - Study subject of this document
- [ClrDebug](https://github.com/lordmilko/ClrDebug) - Managed ICorDebug wrappers
- [netcoredbg](https://github.com/Samsung/netcoredbg) - Alternative C++ implementation
- [vsdbg](https://github.com/microsoft/MIEngine) - Microsoft's DAP debugger

**Books:**
- "Debugging Applications" by John Robbins
- "Advanced .NET Debugging" by Mario Hewardt
- ".NET Internals" blog posts by Matt Warren

---

*Document created from hands-on study of SharpDbg codebase*
*Study Date: January 2026*
*SharpDbg Version: Latest main branch*
