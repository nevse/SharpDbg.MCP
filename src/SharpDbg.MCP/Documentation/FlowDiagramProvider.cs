namespace SharpDbg.MCP.Documentation;

public class FlowDiagramProvider
{
    private readonly DocumentationLoader _loader;
    private readonly Dictionary<string, string> _flowMappings;

    public FlowDiagramProvider(DocumentationLoader loader)
    {
        _loader = loader;
        _flowMappings = BuildFlowMappings();
    }

    private Dictionary<string, string> BuildFlowMappings()
    {
        // Map common operation names to section titles in the documentation
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Exact matches from documentation
            ["setting a breakpoint"] = "Flow 1: Setting a Breakpoint",
            ["set breakpoint"] = "Flow 1: Setting a Breakpoint",
            ["breakpoint"] = "Flow 1: Setting a Breakpoint",

            ["hitting a breakpoint"] = "Flow 2: Hitting a Breakpoint",
            ["hit breakpoint"] = "Flow 2: Hitting a Breakpoint",
            ["breakpoint hit"] = "Flow 2: Hitting a Breakpoint",

            ["evaluating an expression"] = "Flow 3: Evaluating an Expression",
            ["evaluate expression"] = "Flow 3: Evaluating an Expression",
            ["expression evaluation"] = "Flow 3: Evaluating an Expression",
            ["eval"] = "Flow 3: Evaluating an Expression",

            // General concepts
            ["stepping"] = "Stepping",
            ["step"] = "Stepping",
            ["step over"] = "Stepping",
            ["step into"] = "Stepping",
            ["step out"] = "Stepping",

            ["variable inspection"] = "Variable Inspection",
            ["variables"] = "Variable Inspection",
            ["inspect variables"] = "Variable Inspection",

            ["attach"] = "Process Management",
            ["attach to process"] = "Process Management",
            ["process attachment"] = "Process Management",

            ["dap"] = "Debug Adapter Protocol (DAP)",
            ["debug adapter protocol"] = "Debug Adapter Protocol (DAP)",
            ["protocol"] = "Debug Adapter Protocol (DAP)",

            ["icordebug"] = "The Foundation: ICorDebug API",
            ["icordebugeval"] = "ICorDebugEval: The Magic",
            ["icordebugprocess"] = "Core ICorDebug Interfaces",
            ["icordebugthread"] = "Core ICorDebug Interfaces"
        };
    }

    public string? GetFlow(string operation)
    {
        // Try exact mapping first
        if (_flowMappings.TryGetValue(operation, out var sectionTitle))
        {
            var section = _loader.GetSectionByTitle(sectionTitle);
            if (section != null)
            {
                return FormatFlow(section);
            }
        }

        // Try searching
        var results = _loader.Search(operation);
        if (results.Count > 0)
        {
            // Look for "Flow" sections first
            var flowSection = results.FirstOrDefault(s => s.Title.Contains("Flow", StringComparison.OrdinalIgnoreCase));
            if (flowSection != null)
            {
                return FormatFlow(flowSection);
            }

            // Otherwise return the best match
            return FormatFlow(results.First());
        }

        return null;
    }

    private static string FormatFlow(DocumentSection section)
    {
        return $"# {section.Title}\n\n{section.Content}";
    }

    public List<string> GetAvailableFlows()
    {
        return _flowMappings.Keys.ToList();
    }
}
