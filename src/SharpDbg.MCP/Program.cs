using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SharpDbg.MCP.Configuration;
using SharpDbg.MCP.Logging;
using SharpDbg.MCP.Tools;

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
                    Name = "SharpDbg MCP Server",
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

        // Initialize tools after logging is configured
        McpTools.Initialize();
        DebuggingTools.Initialize();

        logger.LogInformation("SharpDbg MCP Server v{Version} starting...", config.Version);
        logger.LogInformation("Configuration:");
        logger.LogInformation("  Log Level: {LogLevel}", config.LogLevel);
        logger.LogInformation("  Max Sessions: {MaxSessions}", config.MaxConcurrentSessions);
        logger.LogInformation("  Operation Timeout: {Timeout}s", config.OperationTimeoutSeconds);
        logger.LogInformation("  Expression Eval Timeout: {EvalTimeout}ms", config.ExpressionEvaluationTimeoutMs);
        logger.LogInformation("  Diagnostics: {Diagnostics}", config.EnableDiagnostics ? "Enabled" : "Disabled");

        await host.RunAsync();
    }
}
