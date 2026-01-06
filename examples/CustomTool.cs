using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SharpDbg.MCP.Examples;

/// <summary>
/// Example of how to add a custom MCP tool to the server
/// </summary>
/// <remarks>
/// To use this example:
/// 1. Copy this file to the Tools/ directory
/// 2. Change the namespace to SharpDbg.MCP.Tools
/// 3. Rebuild the project
/// 4. The tool will automatically be discovered and registered
/// </remarks>
[McpServerToolType]
public static class CustomToolExample
{
    /// <summary>
    /// Example tool that performs a custom operation
    /// </summary>
    /// <param name="input">Input parameter</param>
    /// <returns>JSON response</returns>
    [McpServerTool, Description("Example custom tool that demonstrates the MCP tool pattern")]
    public static string ExampleTool(string input)
    {
        try
        {
            // 1. Validate input
            if (string.IsNullOrWhiteSpace(input))
            {
                var errorResponse = new
                {
                    success = false,
                    error = "Input cannot be empty"
                };
                return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
            }

            // 2. Perform your custom logic here
            var result = ProcessInput(input);

            // 3. Return JSON response
            var response = new
            {
                success = true,
                input = input,
                result = result,
                timestamp = DateTime.UtcNow
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            // 4. Handle errors gracefully
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };
            return JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Example tool with multiple parameters
    /// </summary>
    [McpServerTool, Description("Example tool with multiple parameters")]
    public static string ExampleToolMultiParam(string name, int count, bool enabled)
    {
        try
        {
            var response = new
            {
                success = true,
                parameters = new
                {
                    name,
                    count,
                    enabled
                },
                message = $"Processed {count} items for {name} (enabled={enabled})"
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

    /// <summary>
    /// Example async tool (if you need to call async APIs)
    /// </summary>
    [McpServerTool, Description("Example async tool")]
    public static string ExampleAsyncTool(string resource)
    {
        try
        {
            // For async operations, call .GetAwaiter().GetResult() in MCP tools
            // (MCP tools must be synchronous, but can call async code internally)
            var result = FetchDataAsync(resource).GetAwaiter().GetResult();

            var response = new
            {
                success = true,
                resource,
                data = result
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

    // Helper methods

    private static string ProcessInput(string input)
    {
        // Your custom logic here
        return input.ToUpperInvariant();
    }

    private static async Task<string> FetchDataAsync(string resource)
    {
        // Simulate async operation
        await Task.Delay(100);
        return $"Data for {resource}";
    }
}

/// <summary>
/// Best Practices for Custom MCP Tools:
///
/// 1. **Attribute Requirements**:
///    - Mark the class with [McpServerToolType]
///    - Mark tool methods with [McpServerTool] and [Description]
///    - Methods must be public static
///    - Methods must return string (JSON)
///
/// 2. **Input Validation**:
///    - Always validate parameters before use
///    - Return clear error messages for invalid input
///    - Use InputValidation class for common validations
///
/// 3. **Error Handling**:
///    - Wrap tool logic in try-catch
///    - Return JSON error responses (don't throw exceptions to MCP framework)
///    - Include meaningful error messages
///
/// 4. **Response Format**:
///    - Always return JSON
///    - Include "success" boolean field
///    - Include "error" field for failures
///    - Use System.Text.Json for serialization
///    - Set WriteIndented = true for readability
///
/// 5. **Async Operations**:
///    - MCP tool methods must be synchronous
///    - Call async methods with .GetAwaiter().GetResult()
///    - Handle cancellation and timeouts appropriately
///
/// 6. **Documentation**:
///    - Add XML comments for IntelliSense
///    - Add clear Description attribute for AI visibility
///    - Document parameters and return format
///    - Add tool to README.md "Available Tools" section
///
/// 7. **Testing**:
///    - Add unit tests in SharpDbg.MCP.Tests
///    - Test happy path and error cases
///    - Test with actual Claude Desktop
///
/// 8. **Logging**:
///    - Use McpLogger for structured logging
///    - Log tool invocations, errors, and important events
///    - Include context (session IDs, parameters)
/// </summary>
public static class BestPracticesExample
{
    // See CustomToolExample above for implementation
}
