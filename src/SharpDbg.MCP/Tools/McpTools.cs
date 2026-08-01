using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol.Server;

using SharpDbg.MCP.Documentation;

namespace SharpDbg.MCP.Tools;

/// <summary>
/// Collection of MCP tools for documentation search
/// </summary>
[McpServerToolType]
public sealed class McpTools
{
    private readonly DocumentationLoader _loader;
    private readonly ConceptIndex _conceptIndex;
    private readonly FlowDiagramProvider _flowProvider;

    public McpTools(DocumentationLoader loader, ConceptIndex conceptIndex, FlowDiagramProvider flowProvider)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(conceptIndex);
        ArgumentNullException.ThrowIfNull(flowProvider);

        _loader = loader;
        _conceptIndex = conceptIndex;
        _flowProvider = flowProvider;
    }

    [McpServerTool, Description("Search the .NET debugger documentation for concepts related to the query")]
    public string SearchDebuggingConcepts(string query)
    {
        var results = _loader.Search(query);

        if (results.Count == 0)
        {
            return $"No results found for query: {query}";
        }

        var response = new
        {
            query,
            result_count = results.Count,
            results = results.Select(r => new
            {
                title = r.Title,
                level = r.Level,
                preview = r.Content.Length > 300 ? r.Content.Substring(0, 300) + "..." : r.Content,
                full_content = r.Content
            }).ToList()
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    // Name pinned: the SDK would turn ExplainICorDebugInterface into explain_i_cor_debug_interface
    [McpServerTool(Name = "explain_icordebug_interface")]
    public string ExplainICorDebugInterface(string interface_name)
    {
        var results = _loader.Search(interface_name);

        if (results.Count == 0)
        {
            return $"No information found for: {interface_name}";
        }

        var bestMatch = results.First();

        var response = new
        {
            interface_name,
            title = bestMatch.Title,
            explanation = bestMatch.Content
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get the step-by-step flow for a debugging operation such as 'setting a breakpoint' or 'evaluating an expression'")]
    public string GetDebuggingFlow(string operation)
    {
        var flow = _flowProvider.GetFlow(operation);

        if (flow == null)
        {
            return $"No flow found for operation: {operation}\n\nAvailable flows: {string.Join(", ", _flowProvider.GetAvailableFlows())}";
        }

        return flow;
    }

    [McpServerTool, Description("List all available debugging concepts organized by category")]
    public string ListDebuggingConcepts()
    {
        var concepts = _conceptIndex.GetAllConcepts();

        var response = new
        {
            total_categories = concepts.Count,
            categories = concepts.Select(kvp => new
            {
                category = kvp.Key,
                concepts = kvp.Value
            }).ToList()
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }
}
