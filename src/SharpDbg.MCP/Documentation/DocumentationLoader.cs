using System.Reflection;
using System.Text;

using Markdig;
using Markdig.Syntax;

namespace SharpDbg.MCP.Documentation;

public class DocumentationLoader
{
    private readonly string _documentContent;
    private readonly List<DocumentSection> _sections;
    private readonly Dictionary<string, List<DocumentSection>> _index;

    public DocumentationLoader()
    {
        // Load embedded resource
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "SharpDbg.MCP.Data.how_dotnet_debuggers_work.md";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not find embedded resource: {resourceName}");
        using var reader = new StreamReader(stream);
        _documentContent = reader.ReadToEnd();

        // Parse document
        _sections = ParseDocument(_documentContent);

        // Build search index
        _index = BuildIndex(_sections);
    }

    private List<DocumentSection> ParseDocument(string markdown)
    {
        var sections = new List<DocumentSection>();
        var document = Markdown.Parse(markdown);

        DocumentSection? currentSection = null;
        var contentBuilder = new StringBuilder();

        foreach (var block in document)
        {
            if (block is HeadingBlock heading)
            {
                // Save previous section if it exists
                if (currentSection != null)
                {
                    currentSection.Content = contentBuilder.ToString().Trim();
                    sections.Add(currentSection);
                    contentBuilder.Clear();
                }

                // Start new section
                var title = GetHeadingText(heading);
                currentSection = new DocumentSection
                {
                    Level = heading.Level,
                    Title = title,
                    Content = string.Empty
                };
            }
            else if (currentSection != null)
            {
                // Add content to current section
                using var writer = new StringWriter();
                var renderer = new Markdig.Renderers.Normalize.NormalizeRenderer(writer);
                renderer.Write(block);
                contentBuilder.AppendLine(writer.ToString());
            }
        }

        // Add final section
        if (currentSection != null && contentBuilder.Length > 0)
        {
            currentSection.Content = contentBuilder.ToString().Trim();
            sections.Add(currentSection);
        }

        return sections;
    }

    private static string GetHeadingText(HeadingBlock heading)
    {
        var sb = new StringBuilder();
        if (heading.Inline != null)
        {
            foreach (var inline in heading.Inline)
            {
                sb.Append(inline.ToString());
            }
        }
        return sb.ToString();
    }

    private Dictionary<string, List<DocumentSection>> BuildIndex(List<DocumentSection> sections)
    {
        var index = new Dictionary<string, List<DocumentSection>>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            // Index by keywords from title and content
            var keywords = ExtractKeywords(section.Title + " " + section.Content);

            foreach (var keyword in keywords)
            {
                if (!index.ContainsKey(keyword))
                {
                    index[keyword] = new List<DocumentSection>();
                }
                if (!index[keyword].Contains(section))
                {
                    index[keyword].Add(section);
                }
            }
        }

        return index;
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Split by various delimiters
        var words = text.Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ':', ';', '(', ')', '[', ']', '{', '}', '`', '*', '#' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Common stop words to exclude
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by", "from", "as", "is", "was", "are", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did", "will", "would", "should", "could", "may", "might", "can", "this", "that", "these", "those", "it", "its", "which", "who", "what", "where", "when", "why", "how"
        };

        foreach (var word in words)
        {
            if (word.Length >= 3 && !stopWords.Contains(word))
            {
                keywords.Add(word);
            }
        }

        return keywords;
    }

    public List<DocumentSection> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<DocumentSection>();
        }

        var queryKeywords = ExtractKeywords(query);
        var sectionScores = new Dictionary<DocumentSection, int>();

        // Score sections by keyword matches
        foreach (var keyword in queryKeywords)
        {
            if (_index.TryGetValue(keyword, out var matchingSections))
            {
                foreach (var section in matchingSections)
                {
                    sectionScores[section] = sectionScores.GetValueOrDefault(section) + 1;
                }
            }
        }

        // Also do direct substring search (case-insensitive)
        foreach (var section in _sections)
        {
            if (section.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                section.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                sectionScores[section] = sectionScores.GetValueOrDefault(section) + 10; // Higher score for direct match
            }
        }

        // Return sections sorted by score
        return sectionScores
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .Take(10)
            .ToList();
    }

    public List<DocumentSection> GetAllSections()
    {
        return _sections.ToList();
    }

    public DocumentSection? GetSectionByTitle(string title)
    {
        return _sections.FirstOrDefault(s =>
            s.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
    }
}

public class DocumentSection
{
    public int Level { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }

    public override bool Equals(object? obj)
    {
        return obj is DocumentSection other && Title == other.Title;
    }

    public override int GetHashCode()
    {
        return Title.GetHashCode();
    }
}
