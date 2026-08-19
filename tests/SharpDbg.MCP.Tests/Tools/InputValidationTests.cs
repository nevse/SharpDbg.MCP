using Microsoft.VisualStudio.TestTools.UnitTesting;

using SharpDbg.MCP.Debugging;
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
    public void ValidateBreakpointId_ValidId_DoesNotThrow()
    {
        InputValidation.ValidateBreakpointId(1);
        InputValidation.ValidateBreakpointId(int.MaxValue);
    }

    [TestMethod]
    public void ValidateBreakpointId_InvalidIds_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateBreakpointId(0));
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateBreakpointId(-1));
    }

    [TestMethod]
    public void ValidateHitCondition_ValidForms_DoNotThrow()
    {
        InputValidation.ValidateHitCondition(null);
        InputValidation.ValidateHitCondition("");
        InputValidation.ValidateHitCondition("5");
        InputValidation.ValidateHitCondition("==5");
        InputValidation.ValidateHitCondition(">5");
        InputValidation.ValidateHitCondition(">=5");
        InputValidation.ValidateHitCondition("<5");
        InputValidation.ValidateHitCondition("<=5");
        InputValidation.ValidateHitCondition("%5");
        InputValidation.ValidateHitCondition("  >= 5 ".Replace(" ", string.Empty));
    }

    [TestMethod]
    public void ValidateHitCondition_Unparseable_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("every other"));
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition(">"));
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("=5"));
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("5x"));
    }

    [TestMethod]
    public void ValidateHitCondition_ModuloWithoutPositiveInterval_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("%0"));
        Assert.ThrowsExactly<ArgumentException>(() => InputValidation.ValidateHitCondition("%-2"));
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

    [TestMethod]
    public void ParseExceptionBreakMode_KnownModes_ParsesCaseInsensitively()
    {
        Assert.AreEqual(ExceptionBreakMode.Always, InputValidation.ParseExceptionBreakMode("always"));
        Assert.AreEqual(ExceptionBreakMode.Always, InputValidation.ParseExceptionBreakMode(" Always "));
        Assert.AreEqual(ExceptionBreakMode.Never, InputValidation.ParseExceptionBreakMode("NEVER"));
    }

    [TestMethod]
    public void ParseExceptionBreakMode_UnknownMode_ThrowsInsteadOfDefaulting()
    {
        // Falling back to a default would silently change whether the debuggee stops
        foreach (var mode in new[] { "", "  ", "sometimes", "user-unhandled", "unhandled only" })
        {
            try
            {
                InputValidation.ParseExceptionBreakMode(mode);
                Assert.Fail($"Expected ArgumentException for mode '{mode}'");
            }
            catch (ArgumentException) { }
        }
    }

    /// <summary>
    /// The four the debugger can actually distinguish. 'unhandled' was refused here until the move off
    /// SharpDbg, whose stops carried no way to tell a handled exception from one that kills the program.
    /// </summary>
    [TestMethod]
    public void ParseExceptionBreakMode_AcceptsEveryModeTheDebuggerSupports()
    {
        Assert.AreEqual(ExceptionBreakMode.Always, InputValidation.ParseExceptionBreakMode("always"));
        Assert.AreEqual(ExceptionBreakMode.UserUnhandled, InputValidation.ParseExceptionBreakMode("user_unhandled"));
        Assert.AreEqual(ExceptionBreakMode.Unhandled, InputValidation.ParseExceptionBreakMode("unhandled"));
        Assert.AreEqual(ExceptionBreakMode.Never, InputValidation.ParseExceptionBreakMode("never"));

        // Case and padding are the caller's, not ours
        Assert.AreEqual(ExceptionBreakMode.UserUnhandled, InputValidation.ParseExceptionBreakMode("  USER_Unhandled "));
    }

    [TestMethod]
    public void ValidateWaitTimeout_ValidTimeouts_DoesNotThrow()
    {
        InputValidation.ValidateWaitTimeout(1);
        InputValidation.ValidateWaitTimeout(10_000);
        InputValidation.ValidateWaitTimeout(300_000);
    }

    [TestMethod]
    public void ValidateWaitTimeout_InvalidTimeouts_ThrowsArgumentException()
    {
        try
        {
            InputValidation.ValidateWaitTimeout(0);
            Assert.Fail("Expected ArgumentException for timeout 0");
        }
        catch (ArgumentException) { }

        try
        {
            InputValidation.ValidateWaitTimeout(-1);
            Assert.Fail("Expected ArgumentException for negative timeout");
        }
        catch (ArgumentException) { }

        // A wait longer than the client's own request timeout would leave the caller with nothing
        try
        {
            InputValidation.ValidateWaitTimeout(300_001);
            Assert.Fail("Expected ArgumentException for a timeout past the maximum");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public void ValidateProgramPath_ExistingFile_ReturnsAnAbsolutePath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"launch-{Guid.NewGuid():N}.dll");
        File.WriteAllText(file, string.Empty);

        try
        {
            var validated = InputValidation.ValidateProgramPath(file);

            Assert.IsTrue(Path.IsPathRooted(validated));
            Assert.IsTrue(File.Exists(validated));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void ValidateProgramPath_MissingFile_SaysWhereItLooked()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.dll");

        var error = Assert.Throws<ArgumentException>(() => InputValidation.ValidateProgramPath(missing));

        StringAssert.Contains(error.Message, missing);
    }

    /// <summary>
    /// Handing over a project rather than its build output is the mistake worth naming: the debugger
    /// would try to run the .csproj and fail with something that explains nothing.
    /// </summary>
    [TestMethod]
    public void ValidateProgramPath_ProjectFile_SaysToBuildItFirst()
    {
        var error = Assert.Throws<ArgumentException>(
            () => InputValidation.ValidateProgramPath("/somewhere/MyApp.csproj"));

        StringAssert.Contains(error.Message, "Build it");
    }

    [TestMethod]
    public void ValidateProgramPath_Empty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateProgramPath(""));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateProgramPath("   "));
    }

    [TestMethod]
    public void ValidateWorkingDirectory_NullMeansTheProgramsOwnDirectory()
    {
        InputValidation.ValidateWorkingDirectory(null);
    }

    [TestMethod]
    public void ValidateWorkingDirectory_MissingDirectory_ThrowsArgumentException()
    {
        InputValidation.ValidateWorkingDirectory(Path.GetTempPath());

        Assert.Throws<ArgumentException>(
            () => InputValidation.ValidateWorkingDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [TestMethod]
    public void ValidateOutputLineCount_MustBePositive()
    {
        InputValidation.ValidateOutputLineCount(1);

        Assert.Throws<ArgumentException>(() => InputValidation.ValidateOutputLineCount(0));
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateOutputLineCount(-1));
    }
}
