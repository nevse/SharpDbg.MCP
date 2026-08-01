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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [McpServerTool, Description("Set a breakpoint at a specific file and line number")]
    public static string SetBreakpoint(string file_path, int line)
    {
        try
        {
            // Validate input
            InputValidation.ValidateFilePath(file_path);
            InputValidation.ValidateLineNumber(line);

            var session = _sessionManager.Value.GetOrCreateCurrentSession();

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use AttachToProcess first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var result = session.SetBreakpoint(file_path, line);

            var response = new
            {
                success = true,
                breakpoint = new
                {
                    id = result.Id,
                    file_path = result.FilePath,
                    line = result.Line,
                    verified = result.Verified,
                    message = result.Message
                }
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
                    : "Process was already running; nothing to resume. Use GetProcessStatus to check whether it has stopped."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            session.Pause();

            var response = new
            {
                success = true,
                message = "Process execution paused. Use GetProcessStatus to check current location."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [McpServerTool, Description("Evaluate a C# expression in the context of a stack frame")]
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
                    error = "Not attached to a process. Use AttachToProcess first."
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
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
