using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using ICorDebugSharp;

namespace SharpDbg.MCP.Tools;

/// <summary>
/// Turns ICorDebug failures into something a caller can act on. Without this the client only sees
/// the raw COM text - "Returned from a call to Continue that was not matched with a stopping event.
/// (0x8013132F)" - which describes the symptom and nothing about what to do next.
/// The HRESULT values come from ICorDebugSharp's own constants rather than being written out here.
/// </summary>
public static partial class DebuggerErrors
{
    private static readonly Dictionary<int, string> Explanations = new()
    {
        [Cor.CORDBG_E_PROCESS_TERMINATED] =
            "The debuggee has exited. Attach to another process to keep debugging.",

        [Cor.CORDBG_E_PROCESS_DETACHED] =
            "The debugger is no longer attached to the process. Attach again.",

        [Cor.CORDBG_E_DEBUGGER_ALREADY_ATTACHED] =
            "Another debugger is already attached to that process. Only one debugger can attach at " +
            "a time, so close the other one - an IDE session, or a previous run of this server.",

        [Cor.CORDBG_E_PROCESS_NOT_SYNCHRONIZED] =
            "This needs the debuggee to be stopped. Wait for a stop with wait_for_stop, or stop it " +
            "yourself with pause_execution, and then retry.",

        [Cor.CORDBG_E_SUPERFLOUS_CONTINUE] =
            "The process was already running, so there was nothing to resume. Check " +
            "get_process_status before continuing. Note that a preceding expression evaluation can " +
            "leave the process suspended while the debugger considers it running.",

        [Cor.CORDBG_E_HANDLE_HAS_BEEN_DISPOSED] =
            "The debugger is holding a handle it has already released, which an earlier " +
            "evaluation - expand_variable on a member that has to be evaluated, or " +
            "evaluate_expression - can cause. This never recovers on retry: the debuggee stays " +
            "suspended until detach_from_process releases it, after which you can attach again.",

        [Cor.CORDBG_E_IL_VAR_NOT_AVAILABLE] =
            "The variable does not exist at this instruction. It is either out of scope or the " +
            "target was built optimized, which removes locals; build the debuggee with " +
            "<Optimize>false</Optimize> to read them.",

        [Cor.CORDBG_E_FIELD_NOT_AVAILABLE] =
            "The field is not available in the target. This usually means the type is a generic " +
            "instantiation the runtime has not laid out yet.",

        [Cor.CORDBG_E_STATIC_VAR_NOT_AVAILABLE] =
            "The static field has no storage yet, because its type has not been initialized in the " +
            "target. Let the program run until the type is used and try again.",

        [Cor.CORDBG_E_CLASS_NOT_LOADED] =
            "The type has not been loaded in the target yet. Let the program run further and retry.",

        [Cor.CORDBG_E_CODE_NOT_AVAILABLE] =
            "The method has no code in the target yet, so it cannot be inspected or have a " +
            "breakpoint bound. It is either not jitted yet or has been optimized away.",

        [Cor.CORDBG_E_BAD_THREAD_STATE] =
            "The thread cannot be used for this. A thread with no managed frames - a native or " +
            "runtime thread - has no stack to walk and no frame to evaluate in; get the current " +
            "thread from get_process_status while stopped.",

        [Cor.CORDBG_E_FUNC_EVAL_BAD_START_POINT] =
            "Code cannot be run in the target from where this thread is stopped. Evaluation needs a " +
            "thread stopped at a managed safe point, such as a breakpoint.",

        [Cor.CORDBG_E_FUNC_EVAL_NOT_COMPLETE] =
            "An earlier evaluation in the target has not finished, so a new one cannot start.",

        [Cor.CORDBG_E_PAST_END_OF_STACK] =
            "The stack walk ran off the end of the stack. The frame is stale: frame ids only apply " +
            "to the stop they were taken in, so call get_stack_trace again after every stop.",

        [Cor.CORDBG_E_NON_NATIVE_FRAME] =
            "The frame is a managed frame and does not support this operation.",

        [Cor.CORDBG_E_UNRECOVERABLE_ERROR] =
            "The debugging session is broken and cannot be used any more. Detach with " +
            "detach_from_process and attach again."
    };

    /// <summary>
    /// The explanation for a known ICorDebug HRESULT, or null when the failure is something else
    /// </summary>
    public static string? Explain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (Explanations.TryGetValue(current.HResult, out var explanation))
                return explanation;
        }

        // A wrapper keeps its own HResult, so the original may only survive as text in the message
        for (var current = exception; current != null; current = current.InnerException)
        {
            foreach (var match in HResultInText().Matches(current.Message).Cast<Match>())
            {
                var value = uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                if (Explanations.TryGetValue(unchecked((int)value), out var explanation))
                    return explanation;
            }
        }

        return null;
    }

    /// <summary>
    /// The error response every tool returns on failure. The raw message is always kept, so nothing
    /// the debugger said is lost, and explanation is null for failures that are not ICorDebug ones.
    /// </summary>
    public static string ErrorResponse(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var response = new
        {
            success = false,
            error = exception.Message,
            explanation = Explain(exception)
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    [GeneratedRegex(@"0x([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex HResultInText();
}
