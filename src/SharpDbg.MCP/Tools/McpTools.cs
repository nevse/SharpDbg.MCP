using SharpDbg.MCP.Documentation;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SharpDbg.MCP.Tools;

/// <summary>
/// Collection of MCP tools for documentation search
/// </summary>
[McpServerToolType]
public static class McpTools
{
    private static readonly Lazy<DocumentationLoader> _loader = new(() => new DocumentationLoader());
    private static readonly Lazy<ConceptIndex> _conceptIndex = new(() => new ConceptIndex(_loader.Value));
    private static readonly Lazy<FlowDiagramProvider> _flowProvider = new(() => new FlowDiagramProvider(_loader.Value));

    public static void Initialize()
    {
        // Force initialization of lazy instances
        _ = _loader.Value;
        _ = _conceptIndex.Value;
        _ = _flowProvider.Value;
    }

    [McpServerTool, Description("Search the .NET debugger documentation for concepts related to the query")]
    public static string SearchDebuggingConcepts(string query)
    {
        var results = _loader.Value.Search(query);

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

    [McpServerTool, Description("Explain a specific ICorDebug interface or debugging concept")]
    public static string ExplainICorDebugInterface(string interface_name)
    {
        var results = _loader.Value.Search(interface_name);

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
    public static string GetDebuggingFlow(string operation)
    {
        var flow = _flowProvider.Value.GetFlow(operation);

        if (flow == null)
        {
            return $"No flow found for operation: {operation}\n\nAvailable flows: {string.Join(", ", _flowProvider.Value.GetAvailableFlows())}";
        }

        return flow;
    }

    [McpServerTool, Description("List all available debugging concepts organized by category")]
    public static string ListDebuggingConcepts()
    {
        var concepts = _conceptIndex.Value.GetAllConcepts();

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
