using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Debugging;
using SharpDbg.MCP.Documentation;
using SharpDbg.MCP.Logging;

namespace SharpDbg.MCP;

class Program
{
    static async Task Main(string[] args)
    {
        // Load configuration from environment
        var config = ServerConfiguration.LoadFromEnvironment();

        // Validate configuration
        var validationError = config.Validate();
        if (validationError != null)
        {
            Console.Error.WriteLine($"Configuration error: {validationError}");
            Environment.Exit(1);
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Register configuration as singleton
        builder.Services.AddSingleton(config);

        // The SDK builds a tool class per call, so what the tools hold has to outlive the call:
        // sessions would otherwise be created and dropped one tool call at a time.
        builder.Services.AddSingleton<DebugSessionManager>();
        builder.Services.AddSingleton<ProcessDiscovery>();
        builder.Services.AddSingleton<DocumentationLoader>();
        builder.Services.AddSingleton<ConceptIndex>();
        builder.Services.AddSingleton<FlowDiagramProvider>();

        // Configure logging to stderr (required for MCP stdio transport)
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(config.LogLevel);

        // Configure MCP server
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    // Clients show this to the user, so it is the package name they installed
                    Name = "DotnetDebugger.Mcp",
                    Version = config.Version
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();

        // Initialize logging infrastructure
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("SharpDbg.MCP");
        McpLogger.Initialize(logger);

        // Build the documentation index up front rather than on the first search, so a cold call is
        // not the one that pays for loading it
        _ = host.Services.GetRequiredService<ConceptIndex>();
        _ = host.Services.GetRequiredService<FlowDiagramProvider>();

        logger.LogInformation("DotnetDebugger.Mcp v{Version} starting...", config.Version);
        logger.LogInformation("Configuration:");
        logger.LogInformation("  Log Level: {LogLevel}", config.LogLevel);
        logger.LogInformation("  Max Sessions: {MaxSessions}", config.MaxConcurrentSessions);
        logger.LogInformation("  Operation Timeout: {Timeout}s", config.OperationTimeoutSeconds);
        logger.LogInformation("  Expression Eval Timeout: {EvalTimeout}ms", config.ExpressionEvaluationTimeoutMs);
        logger.LogInformation("  Diagnostics: {Diagnostics}", config.EnableDiagnostics ? "Enabled" : "Disabled");

        await host.RunAsync();
    }
}
