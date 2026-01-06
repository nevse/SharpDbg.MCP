namespace SharpDbg.MCP.Documentation;

public class ConceptIndex
{
    private readonly DocumentationLoader _loader;
    private readonly Dictionary<string, List<string>> _concepts;

    public ConceptIndex(DocumentationLoader loader)
    {
        _loader = loader;
        _concepts = BuildConceptCatalog();
    }

    private Dictionary<string, List<string>> BuildConceptCatalog()
    {
        var concepts = new Dictionary<string, List<string>>();

        // Define major concept categories from the documentation
        concepts["Core Architecture"] = new List<string>
        {
            "The Foundation: ICorDebug API",
            "Architecture Overview",
            "Three-Layer Architecture",
            "Infrastructure Layer",
            "Application Layer",
            "CLI Layer"
        };

        concepts["Debugging Fundamentals"] = new List<string>
        {
            "Breakpoints",
            "Stepping",
            "Variable Inspection",
            "Lazy Resolution",
            "Event-Driven Architecture"
        };

        concepts["Expression Evaluation"] = new List<string>
        {
            "Expression Evaluation Deep Dive",
            "Phase 1: Compilation",
            "Phase 2: Interpretation",
            "ICorDebugEval",
            "Roslyn Integration"
        };

        concepts["Debugger Attributes"] = new List<string>
        {
            "DebuggerDisplay",
            "DebuggerTypeProxy",
            "DebuggerBrowsable",
            "Attribute Discovery",
            "Runtime Evaluation"
        };

        concepts["Debug Adapter Protocol"] = new List<string>
        {
            "Debug Adapter Protocol (DAP)",
            "DAP Message Types",
            "DAP Requests",
            "DAP Events",
            "Protocol Implementation"
        };

        concepts["ICorDebug Interfaces"] = new List<string>
        {
            "ICorDebug",
            "ICorDebugProcess",
            "ICorDebugThread",
            "ICorDebugFrame",
            "ICorDebugValue",
            "ICorDebugEval",
            "ICorDebugStepper"
        };

        concepts["Debugging Flows"] = new List<string>
        {
            "Setting a Breakpoint",
            "Hitting a Breakpoint",
            "Evaluating an Expression",
            "Stepping Through Code",
            "Variable Inspection Flow"
        };

        concepts["Design Patterns"] = new List<string>
        {
            "Event-Driven Architecture",
            "Lazy Resolution",
            "Reference-Based Inspection",
            "Adapter Pattern",
            "Partial Classes"
        };

        concepts["Advanced Topics"] = new List<string>
        {
            "Async Stepping",
            "Symbol Resolution",
            "PDB Files",
            "Sequence Points",
            "Source Link"
        };

        return concepts;
    }

    public Dictionary<string, List<string>> GetAllConcepts()
    {
        return new Dictionary<string, List<string>>(_concepts);
    }

    public List<string> GetConceptsByCategory(string category)
    {
        return _concepts.TryGetValue(category, out var concepts)
            ? new List<string>(concepts)
            : new List<string>();
    }

    public List<string> GetCategories()
    {
        return _concepts.Keys.ToList();
    }

    public List<DocumentSection> FindConceptSections(string concept)
    {
        // Search for sections related to this concept
        return _loader.Search(concept);
    }
}
