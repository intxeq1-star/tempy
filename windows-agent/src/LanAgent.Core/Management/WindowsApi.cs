using System.Runtime.InteropServices;

namespace LanAgent.Core.Management;

/// <summary>Minimal Win32 interop used by the agent (memory status, terminal session APIs).</summary>
public static class WindowsApi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPStr)] public string WinStationName;
        public int State; // 0 = WTSActive
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Ansi)]
    private static extern int WTSEnumerateSessions(
        IntPtr hServer, int Reserved, int Version, ref IntPtr ppSessionInfo, ref int pCount);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    /// <summary>Total/available physical memory in MB. Returns false when unavailable (non-Windows).</summary>
    public static bool TryGetMemory(out ulong totalMb, out ulong availableMb)
    {
        totalMb = 0; availableMb = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var status = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(status)) return false;
            totalMb = status.ullTotalPhys / (1024 * 1024);
            availableMb = status.ullAvailPhys / (1024 * 1024);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ids of active (interactive) sessions, excluding services (session 0).</summary>
    public static List<int> GetActiveInteractiveSessions()
    {
        var result = new List<int>();
        if (!OperatingSystem.IsWindows()) return result;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int count = 0;
            // WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero; WTS_PROTOCOL_TYPE... version 1
            int rc = WTSEnumerateSessions(IntPtr.Zero, 0, 1, ref buffer, ref count);
            if (rc == 0 || buffer == IntPtr.Zero) return result;
            int structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            IntPtr current = buffer;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);
                if (info.State == 0 && info.SessionId != 0) result.Add(info.SessionId); // WTSActive, not services
                current += structSize;
            }
        }
        catch
        {
            // Best effort only.
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
        return result;
    }

    /// <summary>Disconnects the active console session — locks the workstation.</summary>
    public static bool LockWorkstation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF) return false;
            return WTSDisconnectSession(IntPtr.Zero, (int)sessionId, false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Logs off every active interactive session. Returns the number affected.</summary>
    public static int LogoffInteractiveSessions()
    {
        int count = 0;
        foreach (int sessionId in GetActiveInteractiveSessions())
        {
            try { if (WTSLogoffSession(IntPtr.Zero, sessionId, false)) count++; }
            catch { /* continue with the rest */ }
        }
        return count;
    }
}
