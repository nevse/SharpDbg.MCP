using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol.Server;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;

namespace SharpDbg.MCP.Tools;

/// <summary>
/// MCP tools for interactive debugging.
/// The SDK builds one of these per tool call, so everything it depends on is registered as a
/// singleton - a session manager that came and went with the call would lose every session.
/// </summary>
[McpServerToolType]
public sealed class DebuggingTools
{
    private readonly ServerConfiguration _configuration;
    private readonly DebugSessionManager _sessionManager;
    private readonly ProcessDiscovery _processDiscovery;

    public DebuggingTools(
        ServerConfiguration configuration,
        DebugSessionManager sessionManager,
        ProcessDiscovery processDiscovery)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(processDiscovery);

        _configuration = configuration;
        _sessionManager = sessionManager;
        _processDiscovery = processDiscovery;
    }

    // Name pinned: the SDK would turn ListDotNetProcesses into list_dot_net_processes
    [McpServerTool(Name = "list_dotnet_processes"), Description(
        "List all .NET processes currently running on the system. owner tells you whether a process " +
        "belongs to the user running this server: attaching is refused for 'other_user' and " +
        "'unknown' unless SHARPDBG_ALLOW_OTHER_USER_PROCESSES is set.")]
    public string ListDotNetProcesses()
    {
        var processes = _processDiscovery.ListDotNetProcesses();

        var response = new
        {
            count = processes.Count,
            attachable_owners_only = !_configuration.AllowOtherUserProcesses,
            processes = processes.Select(p => new
            {
                process_id = p.ProcessId,
                process_name = p.ProcessName,
                main_module = p.MainModule,
                owner = OwnerName(p.Owner)
            }).ToList()
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string OwnerName(ProcessOwnership.Ownership owner) => owner switch
    {
        ProcessOwnership.Ownership.CurrentUser => "current_user",
        ProcessOwnership.Ownership.OtherUser => "other_user",
        _ => "unknown"
    };

    [McpServerTool, Description(
        "Attach the debugger to a .NET process by process ID. Attaching while another process is " +
        "already being debugged opens a second session instead of failing, up to " +
        "SHARPDBG_MAX_SESSIONS (one by default). The session_id in the response is what the other " +
        "tools take to say which process they mean; it can be omitted while only one session is open.")]
    public string AttachToProcess(int process_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateProcessId(process_id);

            // A debugger can read and change everything in the process it attaches to, so by default
            // only the user's own processes are allowed. Checked before anything else looks at the
            // process, so a refusal does not depend on what the target turns out to be.
            var denyReason = ProcessOwnership.DenyReason(
                ProcessOwnership.Of(process_id),
                _configuration.AllowOtherUserProcesses);

            if (denyReason != null)
            {
                var refusedResponse = new
                {
                    success = false,
                    error = denyReason
                };
                return JsonSerializer.Serialize(refusedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            // Verify it's a .NET process
            if (!_processDiscovery.IsDotNetProcess(process_id))
            {
                var errorResponse = new
                {
                    success = false,
                    error = $"Process {process_id} is not a .NET process or cannot be accessed"
                };
                return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            // A second attach opens a second session rather than failing, up to
            // SHARPDBG_MAX_SESSIONS
            var session = _sessionManager.AcquireForDebuggee(session_id);
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
        "Launch a .NET program under the debugger without running it yet. This is the only way to " +
        "debug what happens at startup: breakpoints set now are in place before the first line " +
        "executes. Pass the built program - the .dll or the executable next to it - not a project " +
        "file. Then set breakpoints and call start_program to run it. The program's output is " +
        "captured rather than printed, and read with get_program_output. A program launched this " +
        "way is killed when the session is detached or closed, though the debugger does not always " +
        "confirm it - both report program_may_be_running when it did not.")]
    public string LaunchProgram(
        string program_path,
        string[]? args = null,
        string? working_directory = null,
        Dictionary<string, string>? environment = null,
        int? session_id = null)
    {
        try
        {
            var program = InputValidation.ValidateProgramPath(program_path);
            InputValidation.ValidateWorkingDirectory(working_directory);

            var session = _sessionManager.AcquireForDebuggee(session_id);
            session.Launch(program, args, working_directory, environment).GetAwaiter().GetResult();

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                program,
                started = false,
                message = "Launched but not started. Set breakpoints now - they will be in place " +
                    "before the program runs - then call start_program."
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Run the program prepared by launch_program. Returns as soon as it is running, which can be " +
        "after it has already hit a breakpoint; use wait_for_stop to catch that. The process id is " +
        "not reported: the debugger does not say what it started.")]
    public string StartProgram(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);
            session.Start();

            var state = session.GetExecutionState();

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                program = state.LaunchedProgram,
                started = true,
                is_running = state.IsRunning,
                current_location = state.CurrentLocation,
                stop_reason = state.StopReason
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Read what the debuggee has written to stdout and stderr, oldest line first. Only a " +
        "launched program's output is captured - a process attached to with attach_to_process keeps " +
        "its own console, and nothing appears here. Returns at most max_lines (default 100) of the " +
        "most recent output; the last 1000 lines are kept.")]
    public string GetProgramOutput(int max_lines = 100, int? session_id = null)
    {
        try
        {
            InputValidation.ValidateOutputLineCount(max_lines);

            var session = _sessionManager.Resolve(session_id);
            var lines = session.ReadOutput(max_lines);

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                count = lines.Count,
                output = lines.Select(l => new { text = l.Text, stream = l.IsError ? "stderr" : "stdout" }).ToList()
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
    public string WaitForStop(int timeout_ms = 10000, int? session_id = null)
    {
        try
        {
            InputValidation.ValidateWaitTimeout(timeout_ms);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string SetExceptionBreakMode(string mode, int? session_id = null)
    {
        try
        {
            var parsed = InputValidation.ParseExceptionBreakMode(mode);

            var session = _sessionManager.Resolve(session_id);
            session.ExceptionBreakMode = parsed;

            var state = session.GetExecutionState();

            var response = new
            {
                success = true,
                session_id = session.SessionId,
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

    [McpServerTool, Description(
        "Get the status of a debug session. Omit session_id unless more than one session is open, in " +
        "which case list_sessions shows which is which.")]
    public string GetProcessStatus(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);
            var state = session.GetExecutionState();

            var response = new
            {
                session_id = session.SessionId,
                is_attached = state.IsAttached,
                process_id = state.ProcessId,
                program = state.LaunchedProgram,
                started = state.Started,
                is_running = state.IsRunning,
                is_stopped = state.IsAttached && state.Started && !state.IsRunning,
                current_location = state.CurrentLocation,
                stop_reason = state.StopReason,
                exception_break_mode = session.ExceptionBreakMode.ToString().ToLowerInvariant(),
                exceptions_seen = state.ExceptionsSeen,
                exceptions_ignored = state.ExceptionsIgnored,
                // The thread to pass to get_stack_trace/step_over/step_into/step_out while stopped
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
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "List the open debug sessions, which is how you find the session_id to pass to the other " +
        "tools. A session is created by attach_to_process or launch_program and lives until " +
        "close_session or detach. started=false means the program is launched and waiting to run.")]
    public string ListSessions()
    {
        try
        {
            var sessions = _sessionManager.GetAllSessions();

            var response = new
            {
                success = true,
                count = sessions.Count,
                max_sessions = _configuration.MaxConcurrentSessions,
                sessions = sessions.Select(s =>
                {
                    var state = s.GetExecutionState();

                    return new
                    {
                        session_id = s.SessionId,
                        process_id = state.ProcessId,
                        program = state.LaunchedProgram,
                        started = state.Started,
                        is_attached = state.IsAttached,
                        is_running = state.IsRunning,
                        stop_reason = state.StopReason,
                        current_location = state.CurrentLocation
                    };
                }).OrderBy(s => s.session_id).ToList()
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return DebuggerErrors.ErrorResponse(ex);
        }
    }

    [McpServerTool, Description(
        "Close a debug session, detaching from its process if it is still attached. A program this " +
        "session launched is killed, but the debugger does not always confirm it: program_may_be_running " +
        "is true when it did not, and the program may have survived, so check it. Use this to free a " +
        "slot when SHARPDBG_MAX_SESSIONS has been " +
        "reached.")]
    public string CloseSession(int session_id)
    {
        try
        {
            // Resolve first so closing an id that does not exist says so, rather than quietly doing
            // nothing
            var session = _sessionManager.Resolve(session_id);
            var processId = session.AttachedProcessId;
            var launchedProgram = session.LaunchedProgram;
            // Read before the close, which resets the phase. A launched program that was never
            // started has no process, so nothing is killed and saying so would be wrong.
            var hasStarted = session.HasStarted;

            var released = _sessionManager.CloseSession(session_id);

            var response = new
            {
                success = true,
                session_id,
                // A launched program the teardown could not account for may have survived it, and
                // nothing downstream can tell from the message alone
                program_may_be_running = launchedProgram != null && hasStarted && !released,
                message = (processId, launchedProgram, hasStarted, released) switch
                {
                    (not null, _, _, true) => $"Session {session_id} closed and detached from process {processId.Value}",
                    (not null, _, _, false) =>
                        $"Session {session_id} closed, but the debugger never confirmed releasing process "
                        + $"{processId.Value}, which may still be suspended",
                    (_, not null, true, true) => $"Session {session_id} closed and {launchedProgram} killed",
                    (_, not null, true, false) =>
                        $"Session {session_id} closed, but the debugger never confirmed terminating "
                        + $"{launchedProgram}, which may still be running",
                    (_, not null, false, _) => $"Session {session_id} closed, {launchedProgram} was never started",
                    _ => $"Session {session_id} closed"
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
        "Detach the debugger from a session's process, leaving that process running. A program the " +
        "session launched is killed instead: it exists only for this session. That kill needs the " +
        "program suspended first, and program_may_be_running is true when the debugger never confirmed " +
        "the terminate, meaning the program may have survived - check it rather than assuming. The session stays " +
        "open and free, so the next attach_to_process or launch_program reuses it rather than " +
        "taking another slot; close_session removes it.")]
    public string DetachFromProcess(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);

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
            var launchedProgram = session.LaunchedProgram;
            // Read before the detach, which resets the phase. A launched program that was never
            // started has no process, so nothing is killed and saying so would be wrong.
            var hasStarted = session.HasStarted;
            var released = session.Detach();

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                // A launched program the teardown could not account for may have survived it, and
                // nothing downstream can tell from the message alone
                program_may_be_running = launchedProgram != null && hasStarted && !released,
                message = (launchedProgram, hasStarted, released) switch
                {
                    (not null, true, true) => $"Killed {launchedProgram}",
                    (not null, true, false) =>
                        $"The debugger never confirmed terminating {launchedProgram}, which may still be running",
                    (not null, false, _) => $"Discarded {launchedProgram}, which was never started",
                    (null, _, true) => $"Successfully detached from process {processId}",
                    _ => $"The debugger never confirmed releasing process {processId}, which may still be suspended"
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
    public string SetBreakpoint(string file_path, int line, string? condition = null, string? hit_condition = null, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateFilePath(file_path);
            InputValidation.ValidateLineNumber(line);
            InputValidation.ValidateHitCondition(hit_condition);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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

    [McpServerTool, Description(
        "Set a breakpoint on a method by name, for when the method is known but the file and line " +
        "are not. " +
        "function_name accepts 'Method', 'Type.Method' or 'Namespace.Type.Method'; the type part " +
        "matches by suffix, so 'Program.Work' matches 'MyApp.Program.Work'. Every method matching " +
        "the name binds, which includes overloads and same-named methods in several assemblies - " +
        "bound_locations reports each place it bound, so check it to see what was actually caught. " +
        "Narrow it with a parameter list, 'Work(int, string)', where C# keywords, nullables and " +
        "generics are understood ('int', 'string?', 'List<int>'), or by generic arity, 'Work<T>'. " +
        "condition and hit_condition work as they do for set_breakpoint, and re-sending resets the " +
        "hit counts of all function breakpoints. " +
        "Like line breakpoints this needs portable PDBs for the target; verified=false with 'No " +
        "functions matching' means the name matched nothing in the modules loaded so far, and it " +
        "will bind by itself if a later assembly contains it. " +
        "Remove it with remove_breakpoint, the same as a line breakpoint.")]
    public string SetFunctionBreakpoint(string function_name, string? condition = null, string? hit_condition = null, int? session_id = null)
    {
        try
        {
            InputValidation.ValidateFunctionName(function_name);
            InputValidation.ValidateHitCondition(hit_condition);

            var session = _sessionManager.Resolve(session_id);

            if (!session.IsAttached)
            {
                var notAttachedResponse = new
                {
                    success = false,
                    error = "Not attached to a process. Use attach_to_process first."
                };
                return JsonSerializer.Serialize(notAttachedResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            var result = session.SetFunctionBreakpoint(function_name, condition, hit_condition);

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                breakpoint = new
                {
                    id = result.Id,
                    function_name = result.FunctionName,
                    verified = result.Verified,
                    bound_locations = result.BoundLocations
                        .Select(l => new { file_path = l.FilePath, line = l.Line })
                        .ToList(),
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

    [McpServerTool, Description(
        "Remove a previously set breakpoint by its ID, whether it was set with set_breakpoint or " +
        "set_function_breakpoint")]
    public string RemoveBreakpoint(int breakpoint_id, int? session_id = null)
    {
        try
        {
            InputValidation.ValidateBreakpointId(breakpoint_id);

            var session = _sessionManager.Resolve(session_id);

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
    public string ListBreakpoints(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);

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
            var functionBreakpoints = session.ListFunctionBreakpoints();

            var response = new
            {
                success = true,
                session_id = session.SessionId,
                count = breakpoints.Count + functionBreakpoints.Count,
                breakpoints = breakpoints.Select(b => new
                {
                    id = b.Id,
                    file_path = b.FilePath,
                    line = b.Line,
                    verified = b.Verified,
                    condition = b.Condition,
                    hit_condition = b.HitCondition,
                    message = b.Message
                }).ToList(),
                function_breakpoints = functionBreakpoints.Select(b => new
                {
                    id = b.Id,
                    function_name = b.FunctionName,
                    verified = b.Verified,
                    bound_locations = b.BoundLocations.Select(l => new { file_path = l.FilePath, line = l.Line }).ToList(),
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
    public string GetStackTrace(int thread_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string GetThreads(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string GetVariables(int frame_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateFrameId(frame_id);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
        "expand further. References come from get_variables, from evaluate_expression and from this " +
        "tool. Expand while still stopped - a reference only applies to the stop it was taken in.")]
    public string ExpandVariable(int variables_reference, int? session_id = null)
    {
        try
        {
            InputValidation.ValidateVariablesReference(variables_reference);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string ContinueExecution(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string PauseExecution(int? session_id = null)
    {
        try
        {
            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string StepOver(int thread_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string StepInto(int thread_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
    public string StepOut(int thread_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateThreadId(thread_id);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
        "Evaluate a C# expression in the context of a stack frame. An expression that runs code in " +
        "the target - a property getter, ToString(), any method call - is allowed and does not cost " +
        "the session. When the result is an object, variables_reference is non-zero and can be " +
        "walked with expand_variable.")]
    public string EvaluateExpression(string expression, int frame_id, int? session_id = null)
    {
        try
        {
            // Validate input
            InputValidation.ValidateExpression(expression);
            InputValidation.ValidateFrameId(frame_id);

            var session = _sessionManager.Resolve(session_id);

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
                session_id = session.SessionId,
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
