# SharpDbg MCP Server Examples

This directory contains examples and templates for extending the SharpDbg MCP Server.

## Available Examples

### 1. Custom MCP Tool (`CustomTool.cs`)

Demonstrates how to add a new MCP tool to the server.

**Features shown:**
- Basic tool structure with validation
- Multi-parameter tools
- Async operations in tools
- Error handling patterns
- Best practices and documentation

**To use:**
1. Copy `CustomTool.cs` to the `Tools/` directory
2. Rename the class and update the namespace to `SharpDbg.MCP.Tools`
3. Implement your custom logic
4. Rebuild the project - the tool will be auto-discovered
5. Add documentation to README.md

## Common Extension Scenarios

### Adding Custom Debugging Commands

If you want to add specialized debugging commands:

```csharp
[McpServerToolType]
public static class CustomDebuggingTools
{
    [McpServerTool, Description("Your custom debugging operation")]
    public static string CustomDebugOperation(int process_id, string operation)
    {
        // Get the current debug session
        var session = DebugSessionManager.GetOrCreateCurrentSession();

        if (!session.IsAttached)
        {
            return JsonError("Not attached to a process");
        }

        // Perform your operation
        // ...

        return JsonSuccess(result);
    }
}
```

### Adding Custom Documentation Searches

To extend the documentation system:

```csharp
[McpServerToolType]
public static class CustomDocumentationTools
{
    [McpServerTool, Description("Search custom documentation")]
    public static string SearchCustomDocs(string query)
    {
        // Load your custom documentation
        var docs = LoadCustomDocumentation();

        // Search and return results
        var results = docs.Search(query);

        return JsonSerializer.Serialize(new
        {
            success = true,
            query,
            results
        });
    }
}
```

### Adding Process Filters

To filter .NET processes by custom criteria:

```csharp
[McpServerToolType]
public static class CustomProcessTools
{
    [McpServerTool, Description("List .NET processes matching criteria")]
    public static string ListFilteredProcesses(string namePattern, int minMemoryMb)
    {
        var discovery = new ProcessDiscovery();
        var allProcesses = discovery.ListDotNetProcesses();

        var filtered = allProcesses
            .Where(p => p.ProcessName.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
            .Where(p => GetProcessMemoryMb(p.ProcessId) >= minMemoryMb)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            success = true,
            count = filtered.Count,
            processes = filtered
        });
    }
}
```

## Testing Your Extensions

1. **Unit Tests**: Add tests to `SharpDbg.MCP.Tests/Tools/`
   ```csharp
   [TestClass]
   public class CustomToolTests
   {
       [TestMethod]
       public void CustomTool_ValidInput_ReturnsSuccess()
       {
           var result = CustomTool.ExampleTool("test");
           Assert.IsTrue(result.Contains("\"success\": true"));
       }
   }
   ```

2. **Manual Testing**: Use the test server script
   ```bash
   ./scripts/test-server.sh
   ```

3. **Integration Testing**: Test with Claude Desktop
   - Configure the server in Claude Desktop
   - Restart Claude Desktop
   - Try invoking your tool in a conversation

## JSON Response Patterns

### Success Response
```json
{
  "success": true,
  "data": { ... },
  "message": "Operation completed"
}
```

### Error Response
```json
{
  "success": false,
  "error": "Error message here",
  "error_code": "OPTIONAL_ERROR_CODE"
}
```

### List Response
```json
{
  "success": true,
  "count": 5,
  "items": [ ... ]
}
```

## Helper Utilities

### JSON Response Helpers

Consider adding these helper methods to your tool class:

```csharp
private static string JsonSuccess(object data)
{
    return JsonSerializer.Serialize(new
    {
        success = true,
        data
    }, new JsonSerializerOptions { WriteIndented = true });
}

private static string JsonError(string message)
{
    return JsonSerializer.Serialize(new
    {
        success = false,
        error = message
    }, new JsonSerializerOptions { WriteIndented = true });
}
```

## Configuration Extensions

To add custom configuration options:

1. Add properties to `ServerConfiguration.cs`
2. Add environment variable loading in `LoadFromEnvironment()`
3. Add validation in `Validate()`
4. Document in main README.md

Example:
```csharp
// In ServerConfiguration.cs
public int CustomTimeoutSeconds { get; set; } = 60;

// In LoadFromEnvironment()
var customTimeout = Environment.GetEnvironmentVariable("SHARPDBG_CUSTOM_TIMEOUT");
if (int.TryParse(customTimeout, out var parsed) && parsed > 0)
{
    config.CustomTimeoutSeconds = parsed;
}
```

## Documentation Extensions

To add custom documentation sections:

1. Add markdown files to `Data/` directory
2. Mark as `EmbeddedResource` in `.csproj`
3. Load in `DocumentationLoader.cs`
4. Index in `ConceptIndex.cs`
5. Expose via MCP tools

## Debugging Extensions

Use these techniques to debug your extensions:

1. **Logging**:
   ```csharp
   McpLogger.LogToolInvocation("MyTool", JsonSerializer.Serialize(parameters));
   McpLogger.LogToolSuccess("MyTool", durationMs);
   McpLogger.LogToolError("MyTool", exception);
   ```

2. **Breakpoints**: Attach a debugger to the MCP server process

3. **Console Output**: Use `Console.Error.WriteLine()` for debug output (goes to stderr)

## Contributing

If you create useful extensions:

1. Consider contributing them back to the main project
2. Follow the guidelines in `CONTRIBUTING.md`
3. Add tests and documentation
4. Submit a pull request

## Questions?

- Check the main README.md
- Read CONTRIBUTING.md
- Open a GitHub Discussion
- Look at existing tool implementations in `Tools/`

Happy extending!
