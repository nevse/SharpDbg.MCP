using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDbg.MCP.Configuration;

namespace SharpDbg.MCP.Tests.Configuration;

[TestClass]
[DoNotParallelize]
public class ServerConfigurationTests
{
    [TestInitialize]
    public void Initialize()
    {
        // Clean up environment variables before each test
        Environment.SetEnvironmentVariable("SHARPDBG_LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("SHARPDBG_MAX_SESSIONS", null);
        Environment.SetEnvironmentVariable("SHARPDBG_OPERATION_TIMEOUT_SECONDS", null);
        Environment.SetEnvironmentVariable("SHARPDBG_ALLOW_OTHER_USER_PROCESSES", null);
        Environment.SetEnvironmentVariable("SHARPDBG_EVAL_TIMEOUT_MS", null);
        Environment.SetEnvironmentVariable("SHARPDBG_ENABLE_DIAGNOSTICS", null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up environment variables after each test
        Environment.SetEnvironmentVariable("SHARPDBG_LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("SHARPDBG_MAX_SESSIONS", null);
        Environment.SetEnvironmentVariable("SHARPDBG_OPERATION_TIMEOUT_SECONDS", null);
        Environment.SetEnvironmentVariable("SHARPDBG_ALLOW_OTHER_USER_PROCESSES", null);
        Environment.SetEnvironmentVariable("SHARPDBG_EVAL_TIMEOUT_MS", null);
        Environment.SetEnvironmentVariable("SHARPDBG_ENABLE_DIAGNOSTICS", null);
    }

    [TestMethod]
    public void LoadFromEnvironment_NoEnvironmentVariables_ReturnsDefaults()
    {
        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(LogLevel.Information, config.LogLevel);
        Assert.AreEqual(1, config.MaxConcurrentSessions);
        Assert.AreEqual(30, config.OperationTimeoutSeconds);
        Assert.AreEqual(false, config.AllowOtherUserProcesses);
        Assert.AreEqual(5000, config.ExpressionEvaluationTimeoutMs);
        Assert.AreEqual(false, config.EnableDiagnostics);
        Assert.AreEqual("1.0.0", config.Version);
    }

    [TestMethod]
    public void LoadFromEnvironment_LogLevel_ParsesCorrectly()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_LOG_LEVEL", "Debug");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(LogLevel.Debug, config.LogLevel);
    }

    [TestMethod]
    public void LoadFromEnvironment_LogLevel_CaseInsensitive()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_LOG_LEVEL", "warning");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(LogLevel.Warning, config.LogLevel);
    }

    [TestMethod]
    public void LoadFromEnvironment_LogLevel_InvalidValue_UsesDefault()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_LOG_LEVEL", "InvalidLevel");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(LogLevel.Information, config.LogLevel);
    }

    [TestMethod]
    public void LoadFromEnvironment_MaxSessions_ParsesCorrectly()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_MAX_SESSIONS", "5");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(5, config.MaxConcurrentSessions);
    }

    [TestMethod]
    public void LoadFromEnvironment_MaxSessions_Zero_UsesDefault()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_MAX_SESSIONS", "0");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(1, config.MaxConcurrentSessions);
    }

    [TestMethod]
    public void LoadFromEnvironment_MaxSessions_Negative_UsesDefault()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_MAX_SESSIONS", "-1");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(1, config.MaxConcurrentSessions);
    }

    [TestMethod]
    public void LoadFromEnvironment_OperationTimeout_ParsesCorrectly()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_OPERATION_TIMEOUT_SECONDS", "60");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(60, config.OperationTimeoutSeconds);
    }

    [TestMethod]
    public void LoadFromEnvironment_AllowOtherUserProcesses_True()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_ALLOW_OTHER_USER_PROCESSES", "true");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(true, config.AllowOtherUserProcesses);
    }

    [TestMethod]
    public void LoadFromEnvironment_AllowOtherUserProcesses_False()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_ALLOW_OTHER_USER_PROCESSES", "false");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(false, config.AllowOtherUserProcesses);
    }

    [TestMethod]
    public void LoadFromEnvironment_EvalTimeout_ParsesCorrectly()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_EVAL_TIMEOUT_MS", "10000");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(10000, config.ExpressionEvaluationTimeoutMs);
    }

    [TestMethod]
    public void LoadFromEnvironment_EnableDiagnostics_True()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_ENABLE_DIAGNOSTICS", "true");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(true, config.EnableDiagnostics);
    }

    [TestMethod]
    public void LoadFromEnvironment_AllVariables_ParsesCorrectly()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SHARPDBG_LOG_LEVEL", "Trace");
        Environment.SetEnvironmentVariable("SHARPDBG_MAX_SESSIONS", "10");
        Environment.SetEnvironmentVariable("SHARPDBG_OPERATION_TIMEOUT_SECONDS", "120");
        Environment.SetEnvironmentVariable("SHARPDBG_ALLOW_OTHER_USER_PROCESSES", "true");
        Environment.SetEnvironmentVariable("SHARPDBG_EVAL_TIMEOUT_MS", "15000");
        Environment.SetEnvironmentVariable("SHARPDBG_ENABLE_DIAGNOSTICS", "true");

        // Act
        var config = ServerConfiguration.LoadFromEnvironment();

        // Assert
        Assert.AreEqual(LogLevel.Trace, config.LogLevel);
        Assert.AreEqual(10, config.MaxConcurrentSessions);
        Assert.AreEqual(120, config.OperationTimeoutSeconds);
        Assert.AreEqual(true, config.AllowOtherUserProcesses);
        Assert.AreEqual(15000, config.ExpressionEvaluationTimeoutMs);
        Assert.AreEqual(true, config.EnableDiagnostics);
    }

    [TestMethod]
    public void Validate_ValidConfiguration_ReturnsNull()
    {
        // Arrange
        var config = new ServerConfiguration
        {
            MaxConcurrentSessions = 5,
            OperationTimeoutSeconds = 60,
            ExpressionEvaluationTimeoutMs = 10000
        };

        // Act
        var result = config.Validate();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Validate_MaxSessionsZero_ReturnsError()
    {
        // Arrange
        var config = new ServerConfiguration { MaxConcurrentSessions = 0 };

        // Act
        var result = config.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("MaxConcurrentSessions"));
    }

    [TestMethod]
    public void Validate_MaxSessionsNegative_ReturnsError()
    {
        // Arrange
        var config = new ServerConfiguration { MaxConcurrentSessions = -1 };

        // Act
        var result = config.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("MaxConcurrentSessions"));
    }

    [TestMethod]
    public void Validate_OperationTimeoutZero_ReturnsError()
    {
        // Arrange
        var config = new ServerConfiguration { OperationTimeoutSeconds = 0 };

        // Act
        var result = config.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("OperationTimeoutSeconds"));
    }

    [TestMethod]
    public void Validate_EvalTimeoutTooLow_ReturnsError()
    {
        // Arrange
        var config = new ServerConfiguration { ExpressionEvaluationTimeoutMs = 50 };

        // Act
        var result = config.Validate();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("ExpressionEvaluationTimeoutMs"));
    }

    [TestMethod]
    public void Validate_EvalTimeoutMinimum_ReturnsNull()
    {
        // Arrange
        var config = new ServerConfiguration { ExpressionEvaluationTimeoutMs = 100 };

        // Act
        var result = config.Validate();

        // Assert
        Assert.IsNull(result);
    }
}
