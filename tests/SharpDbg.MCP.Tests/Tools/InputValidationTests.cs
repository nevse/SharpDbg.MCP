using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpDbg.MCP.Tools;

namespace SharpDbg.MCP.Tests.Tools;

[TestClass]
public class InputValidationTests
{
    [TestMethod]
    public void ValidateProcessId_ValidId_DoesNotThrow()
    {
        InputValidation.ValidateProcessId(1);
        InputValidation.ValidateProcessId(12345);
        InputValidation.ValidateProcessId(int.MaxValue);
    }

    [TestMethod]
    public void ValidateProcessId_InvalidIds_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateProcessId(0);
            Assert.Fail("Expected ArgumentException for processId 0");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateProcessId(-1);
            Assert.Fail("Expected ArgumentException for negative processId");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ValidateThreadId_ValidId_DoesNotThrow()
    {
        InputValidation.ValidateThreadId(0);
        InputValidation.ValidateThreadId(1);
        InputValidation.ValidateThreadId(12345);
    }

    [TestMethod]
    public void ValidateThreadId_Negative_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateThreadId(-1);
            Assert.Fail("Expected ArgumentException for negative threadId");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ValidateFrameId_ValidId_DoesNotThrow()
    {
        InputValidation.ValidateFrameId(1);
        InputValidation.ValidateFrameId(12345);
    }

    [TestMethod]
    public void ValidateFrameId_InvalidIds_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateFrameId(0);
            Assert.Fail("Expected ArgumentException for frameId 0");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateFrameId(-1);
            Assert.Fail("Expected ArgumentException for negative frameId");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ValidateFilePath_ValidPath_DoesNotThrow()
    {
        InputValidation.ValidateFilePath("/path/to/file.cs");
        InputValidation.ValidateFilePath("C:\\path\\to\\file.cs");
        InputValidation.ValidateFilePath("relative/path.cs");
    }

    [TestMethod]
    public void ValidateFilePath_InvalidPaths_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateFilePath(null!);
            Assert.Fail("Expected ArgumentException for null filePath");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateFilePath("");
            Assert.Fail("Expected ArgumentException for empty filePath");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateFilePath("   ");
            Assert.Fail("Expected ArgumentException for whitespace filePath");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ValidateLineNumber_ValidNumber_DoesNotThrow()
    {
        InputValidation.ValidateLineNumber(1);
        InputValidation.ValidateLineNumber(42);
        InputValidation.ValidateLineNumber(int.MaxValue);
    }

    [TestMethod]
    public void ValidateLineNumber_InvalidNumbers_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateLineNumber(0);
            Assert.Fail("Expected ArgumentException for lineNumber 0");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateLineNumber(-1);
            Assert.Fail("Expected ArgumentException for negative lineNumber");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ValidateExpression_ValidExpression_DoesNotThrow()
    {
        InputValidation.ValidateExpression("user.Name");
        InputValidation.ValidateExpression("x + y");
        InputValidation.ValidateExpression("obj.ToString()");
    }

    [TestMethod]
    public void ValidateExpression_InvalidExpressions_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateExpression(null!);
            Assert.Fail("Expected ArgumentException for null expression");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateExpression("");
            Assert.Fail("Expected ArgumentException for empty expression");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateExpression("   ");
            Assert.Fail("Expected ArgumentException for whitespace expression");
        }
        catch (ArgumentException) { }
    }
}
