using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol.Server;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tools;

/// <summary>
/// MCP tools for interactive debugging
/// </summary>
[McpServerToolType]
public static class DebuggingTools
{
    private static ServerConfiguration _configuration = new();
    private static readonly Lazy<DebugSessionManager> _sessionManager = new(() => new DebugSessionManager(_configuration));
    private static readonly Lazy<ProcessDiscovery> _processDiscovery = new(() => new ProcessDiscovery());

    public static void Initialize(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Must be assigned before the lazy session manager is first read
        _configuration = configuration;

        // Force initialization of lazy instances
        _ = _sessionManager.Value;
        _ = _processDiscovery.Value;
    }

    [McpServerTool, Description("List all .NET processes currently running on the system")]
    public static string ListDotNetProcesses()
    {
        var processes = _processDiscovery.Value.ListDotNetProcesses();

        var response = new
        {
            count = processes.Count,
            processes = processes.Select(p => new
            {
                process_id = p.ProcessId,
                process_name = p.ProcessName,
                main_module = p.MainModule
            }).ToList()
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Attach debugger to a .NET process by process ID")]
    public static string AttachToProcess(int process_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateProcessId(process_id);

            // Verify it's a .NET process
            if (!_processDiscovery.Value.IsDotNetProcess(process_id))
            {
                var errorResponse = new
                {
                    success = false,
                    error = $"Process {process_id} is not a .NET process or cannot be accessed"
                };
                return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var session = _sessionManager.Value.GetOrCreateCurrentSession();
            session.Attach(process_id).GetAwaiter().GetResult();

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                process_id = process_id,
                message = $"Successfully attached to process {process_id}"
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Wait for the debuggee to stop and return the same fields as get_process_status. Blocks " +
        "until a breakpoint is hit, a step completes, the process is paused or throws, or the " +
        "process exits, and gives up after timeout_ms (default 10000, maximum 300000). " +
        "Use this after continue_execution or a step instead of polling get_process_status in a " +
        "loop. stopped=false means the process was still running when the wait expired, which is " +
        "not an error - call again to keep waiting. stop_reason 'exited' means the process is gone " +
        "and cannot be stepped or continued.")]
    public static string WaitForStop(int timeout_ms = 10000)
    {
        try
        {
            InputValidation.ValidateWaitTimeout(timeout_ms);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var state = session.WaitForStop(TimeSpan.FromMilliseconds(timeout_ms));

            var response = new
            {
                success = true,
                stopped = state != null,
                timeout_ms,
                current_location = state?.CurrentLocation,
                stop_reason = state?.StopReason,
                // The thread to pass to get_stack_trace/step_over/step_into/step_out while stopped
                stopped_thread_id = state?.StoppedThreadId,
                last_breakpoint = state?.LastBreakpoint == null ? null : new
                {
                    id = state.LastBreakpoint.BreakpointId,
                    file_path = state.LastBreakpoint.FilePath,
                    line = state.LastBreakpoint.Line,
                    thread_id = state.LastBreakpoint.ThreadId
                }
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Control what happens when the debuggee throws. The debugger stops on every first-chance " +
        "exception, including ones the program catches itself, so a program that uses exceptions " +
        "routinely suspends constantly. " +
        "mode 'always' (the default) keeps those stops - stop_reason is 'exception', and " +
        "get_stack_trace on stopped_thread_id shows where it was thrown. " +
        "mode 'never' resumes them automatically, which is what you want when hunting something " +
        "else in a program whose own exceptions are noise. " +
        "There is no mode for unhandled exceptions only, and no filtering by exception type: the " +
        "debugger reports neither the type nor whether the program will handle it without running " +
        "code in the target, which currently leaves the process unable to resume.")]
    public static string SetExceptionBreakMode(string mode)
    {
        try
        {
            var parsed = InputValidation.ParseExceptionBreakMode(mode);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();
            session.ExceptionBreakMode = parsed;

            var state = session.GetExecutionState();

            var response = new
            {
                success = true,
                mode = parsed.ToString().ToLowerInvariant(),
                exceptions_seen = state.ExceptionsSeen,
                exceptions_ignored = state.ExceptionsIgnored
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Get the status of the current debug session")]
    public static string GetProcessStatus()
    {
        var session = _sessionManager.Value.GetOrCreateCurrentSession();
        var state = session.GetExecutionState();

        var response = new
        {
            session_id = session.SessionId,
            is_attached = state.IsAttached,
            process_id = state.ProcessId,
            is_running = state.IsRunning,
            is_stopped = state.IsAttached && !state.IsRunning,
            current_location = state.CurrentLocation,
            stop_reason = state.StopReason,
            exception_break_mode = session.ExceptionBreakMode.ToString().ToLowerInvariant(),
            exceptions_seen = state.ExceptionsSeen,
            exceptions_ignored = state.ExceptionsIgnored,
            // The thread to pass to GetStackTrace/StepOver/StepInto/StepOut while stopped
            stopped_thread_id = state.StoppedThreadId,
            last_breakpoint = state.LastBreakpoint == null ? null : new
            {
                id = state.LastBreakpoint.BreakpointId,
                file_path = state.LastBreakpoint.FilePath,
                line = state.LastBreakpoint.Line,
                thread_id = state.LastBreakpoint.ThreadId
            }
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Detach debugger from the current process")]
    public static string DetachFromProcess()
    {
        try
        {
            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    message = "No process is currently attached"
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var processId = session.AttachedProcessId;
            session.Detach();

            var response = new
            {
                success = true,
                message = $"Successfully detached from process {processId}"
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Set a breakpoint at a specific file and line number. " +
        "Optionally pass condition, a C# expression evaluated in the frame that must be true to " +
        "stop, and/or hit_condition, a hit count in the form '5', '==5', '>5', '>=5', '<5', '<=5' " +
        "or '%5' (every 5th hit). Hit counts include hits where condition was false, and are reset " +
        "whenever any breakpoint in the same file is added, changed or removed. " +
        "Calling this again for the same file and line replaces both conditions, so omitting one " +
        "clears it. " +
        "verified=false means the breakpoint is not bound: usually the path or line does not exist " +
        "in the target, but a breakpoint in an assembly that has not been loaded yet binds by " +
        "itself once it loads, so check list_breakpoints again rather than setting it repeatedly.")]
    public static string SetBreakpoint(string file_path, int line, string? condition = null, string? hit_condition = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateFilePath(file_path);
            InputValidation.ValidateLineNumber(line);
            InputValidation.ValidateHitCondition(hit_condition);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var result = session.SetBreakpoint(file_path, line, condition, hit_condition);

            var response = new
            {
                success = true,
                breakpoint = new
                {
                    id = result.Id,
                    file_path = result.FilePath,
                    line = result.Line,
                    verified = result.Verified,
                    condition = result.Condition,
                    hit_condition = result.HitCondition,
                    message = result.Message
                }
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Remove a previously set breakpoint by its ID")]
    public static string RemoveBreakpoint(int breakpoint_id)
    {
        try
        {
            InputValidation.ValidateBreakpointId(breakpoint_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var removed = session.RemoveBreakpoint(breakpoint_id);

            var response = new
            {
                success = removed,
                breakpoint_id,
                message = removed
                    ? $"Breakpoint {breakpoint_id} removed"
                    : $"No breakpoint with ID {breakpoint_id} is set. Use list_breakpoints to see the current ones."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("List all breakpoints currently set in the debug session")]
    public static string ListBreakpoints()
    {
        try
        {
            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var breakpoints = session.ListBreakpoints();

            var response = new
            {
                success = true,
                count = breakpoints.Count,
                breakpoints = breakpoints.Select(b => new
                {
                    id = b.Id,
                    file_path = b.FilePath,
                    line = b.Line,
                    verified = b.Verified,
                    condition = b.Condition,
                    hit_condition = b.HitCondition,
                    message = b.Message
                }).ToList()
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Get the call stack for a specific thread ID")]
    public static string GetStackTrace(int thread_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var stackFrames = session.GetStackTrace(thread_id);

            var response = new
            {
                success = true,
                thread_id,
                frame_count = stackFrames.Count,
                frames = stackFrames.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    source = f.Source,
                    line = f.Line,
                    column = f.Column,
                    end_line = f.EndLine,
                    end_column = f.EndColumn
                }).ToList()
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Get all threads in the attached process")]
    public static string GetThreads()
    {
        try
        {
            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var threads = session.GetThreads();

            var response = new
            {
                success = true,
                thread_count = threads.Count,
                threads = threads.Select(t => new
                {
                    id = t.Id,
                    name = t.Name
                }).ToList()
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Get local variables for a specific stack frame ID")]
    public static string GetVariables(int frame_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateFrameId(frame_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            // Call async method synchronously for MCP tool
            var variables = session.GetVariables(frame_id).GetAwaiter().GetResult();

            var response = new
            {
                success = true,
                frame_id,
                variable_count = variables.Count,
                variables = variables.Select(v => new
                {
                    name = v.Name,
                    value = v.Value,
                    type = v.Type,
                    variables_reference = v.VariablesReference
                }).ToList()
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Expand a variables_reference into its members, which may themselves carry references to " +
        "expand further. References come from get_variables and from this tool; evaluate_expression " +
        "does not currently return one. Expand while still stopped. " +
        "WARNING: expanding a member whose value has to be evaluated in the process - a record's " +
        "EqualityContract, or anything else of type RuntimeType - currently leaves the debugger " +
        "with a disposed handle, after which continue_execution always fails and the process stays " +
        "suspended. Only detach_from_process releases it. Prefer expanding your own data.")]
    public static string ExpandVariable(int variables_reference)
    {
        try
        {
            InputValidation.ValidateVariablesReference(variables_reference);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            // Call async method synchronously for MCP tool
            var members = session.ExpandVariable(variables_reference).GetAwaiter().GetResult();

            var response = new
            {
                success = true,
                variables_reference,
                member_count = members.Count,
                members = members.Select(v => new
                {
                    name = v.Name,
                    value = v.Value,
                    type = v.Type,
                    variables_reference = v.VariablesReference
                }).ToList()
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Continue execution until the next breakpoint or process exit")]
    public static string ContinueExecution()
    {
        try
        {
            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var resumed = session.Continue();

            var response = new
            {
                success = true,
                resumed,
                message = resumed
                    ? "Process execution resumed. It will run until a breakpoint is hit or the process exits."
                    : "Process was already running; nothing to resume. Use get_process_status to check whether it has stopped."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Pause execution (break into debugger at current location)")]
    public static string PauseExecution()
    {
        try
        {
            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            session.Pause();

            var response = new
            {
                success = true,
                message = "Process execution paused. Use get_process_status to check current location."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Step over the current line (execute and stop at next line in same method)")]
    public static string StepOver(int thread_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            session.StepOver(thread_id);

            var response = new
            {
                success = true,
                message = $"Stepping over on thread {thread_id}. Execution will stop at the next line."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Step into the current line (enter called methods)")]
    public static string StepInto(int thread_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            session.StepInto(thread_id);

            var response = new
            {
                success = true,
                message = $"Stepping into on thread {thread_id}. Execution will stop at the first line of any called method."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description("Step out of the current method (execute until return)")]
    public static string StepOut(int thread_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            session.StepOut(thread_id);

            var response = new
            {
                success = true,
                message = $"Stepping out on thread {thread_id}. Execution will stop after returning from the current method."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Evaluate a C# expression in the context of a stack frame. " +
        "WARNING: an expression that has to run code in the target - a property getter, ToString(), " +
        "any method call - currently leaves the debuggee needing a second continue_execution before " +
        "it really resumes, and after several such evaluations it may not resume at all. The first " +
        "continue_execution still reports resumed=true, so the process looks running while it is " +
        "suspended. This is a SharpDbg defect. Reading fields through get_variables and " +
        "expand_variable does not have this problem.")]
    public static string EvaluateExpression(string expression, int frame_id)
    {
        try
        {
            // Validate input
            InputValidation.ValidateExpression(expression);
            InputValidation.ValidateFrameId(frame_id);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            // Call async method synchronously for MCP tool
            var result = session.EvaluateExpression(expression, frame_id).GetAwaiter().GetResult();

            var response = new
            {
                success = true,
                expression,
                result = result.Result,
                type = result.Type,
                variables_reference = result.VariablesReference
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }
}
