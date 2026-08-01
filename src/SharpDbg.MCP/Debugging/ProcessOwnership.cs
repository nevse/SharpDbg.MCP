using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SharpDbg.MCP.Debugging;

/// <summary>
/// Whether a process belongs to the user running this server, which is what
/// AllowOtherUserProcesses is about. There is no cross-platform API for it, so each platform is
/// handled on its own terms: Linux reads /proc, macOS asks ps, and Windows compares the process
/// token's SID.
/// A process whose owner cannot be established is reported as Unknown rather than as ours - on
/// Windows that is what happens for system and elevated processes, which are exactly the ones the
/// setting exists to keep out.
/// </summary>
public static class ProcessOwnership
{
    /// <summary>
    /// How long a macOS process table reading is reused. Listing processes would otherwise run one
    /// ps per process; a pid does not change owner, so a slightly stale table is harmless.
    /// </summary>
    private static readonly TimeSpan MacProcessTableLifetime = TimeSpan.FromSeconds(2);

    private static readonly object MacTableLock = new();
    private static Dictionary<int, int>? _macProcessTable;
    private static DateTime _macProcessTableTakenAt = DateTime.MinValue;

    public enum Ownership
    {
        /// <summary>Owned by the user running this server</summary>
        CurrentUser,

        /// <summary>Owned by somebody else</summary>
        OtherUser,

        /// <summary>The owner could not be established</summary>
        Unknown
    }

    public static Ownership Of(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsOwnership(processId);

            if (OperatingSystem.IsLinux())
                return Compare(LinuxUserId(processId), LinuxUserId(Environment.ProcessId));

            if (OperatingSystem.IsMacOS())
                return Compare(MacUserId(processId), MacUserId(Environment.ProcessId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or Win32Exception)
        {
            return Ownership.Unknown;
        }

        return Ownership.Unknown;
    }

    /// <summary>
    /// Why an attach must be refused, or null when it is allowed. Separated from the platform work
    /// so the policy itself is obvious: without AllowOtherUserProcesses, only a process known to be
    /// ours may be attached to, so a process whose owner is unknown is refused as well.
    /// </summary>
    public static string? DenyReason(Ownership ownership, bool allowOtherUserProcesses)
    {
        if (allowOtherUserProcesses || ownership == Ownership.CurrentUser)
            return null;

        var detail = ownership == Ownership.OtherUser
            ? "it belongs to another user"
            : "its owner could not be determined, which on Windows is what happens for system and " +
              "elevated processes";

        return $"Refusing to attach because {detail}. Set SHARPDBG_ALLOW_OTHER_USER_PROCESSES=true " +
               "to allow it, keeping in mind that a debugger can read and change everything in the " +
               "process it attaches to.";
    }

    private static Ownership Compare(int? owner, int? currentUser)
    {
        if (owner == null || currentUser == null)
            return Ownership.Unknown;

        return owner == currentUser ? Ownership.CurrentUser : Ownership.OtherUser;
    }

    /// <summary>
    /// The real uid from /proc/[pid]/status, which lists "Uid: real effective saved fs"
    /// </summary>
    private static int? LinuxUserId(int processId)
    {
        var path = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/status";

        if (!File.Exists(path))
            return null;

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                continue;

            var fields = line.AsSpan(4).ToString()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return fields.Length > 0 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var uid)
                ? uid
                : null;
        }

        return null;
    }

    private static int? MacUserId(int processId)
    {
        return MacProcessTable().TryGetValue(processId, out var uid) ? uid : null;
    }

    private static Dictionary<int, int> MacProcessTable()
    {
        lock (MacTableLock)
        {
            if (_macProcessTable != null && DateTime.UtcNow - _macProcessTableTakenAt < MacProcessTableLifetime)
                return _macProcessTable;

            _macProcessTable = ReadMacProcessTable();
            _macProcessTableTakenAt = DateTime.UtcNow;

            return _macProcessTable;
        }
    }

    /// <summary>
    /// Reads pid and real uid for every process from ps. libproc would avoid the child process, but
    /// only by hard-coding the layout of a C struct that this cannot verify.
    /// </summary>
    private static Dictionary<int, int> ReadMacProcessTable()
    {
        var table = new Dictionary<int, int>();

        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-Ao");
        startInfo.ArgumentList.Add("pid=,ruid=");

        using var ps = Process.Start(startInfo);

        if (ps == null)
            return table;

        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (fields.Length >= 2
                && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var uid))
            {
                table[pid] = uid;
            }
        }

        return table;
    }

    [SupportedOSPlatform("windows")]
    private static Ownership WindowsOwnership(int processId)
    {
        // Limited information is enough to open the token and does not need debug privileges
        const uint ProcessQueryLimitedInformation = 0x1000;
        const uint TokenQuery = 0x0008;

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (process == IntPtr.Zero)
            return Ownership.Unknown;

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
                return Ownership.Unknown;

            try
            {
                // WindowsIdentity duplicates the token, so ours is still ours to close
                using var identity = new WindowsIdentity(token);
                using var current = WindowsIdentity.GetCurrent();

                if (identity.User == null || current.User == null)
                    return Ownership.Unknown;

                return identity.User == current.User ? Ownership.CurrentUser : Ownership.OtherUser;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // DllImport rather than LibraryImport: the generated variant needs AllowUnsafeBlocks, which is
    // not worth turning on for three calls made on one platform.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
}
