using SharpDbg.MCP.Debugging;
using System.Text;
namespace SharpDbg.MCP.Tests;

/// <summary>
/// Temporary probe used to prove the Code Quality gate actually fails the job.
/// Deliberately violates using ordering/grouping (dotnet format) and leaves an
/// unused local (CS0219, an error under TreatWarningsAsErrors).
/// </summary>
internal static class GateProbe
{
    public static void Probe()
    {
        var unused = 42;
    }
}
