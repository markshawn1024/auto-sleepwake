using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WakeGlass.Core;

public sealed class PowerService : IStagePower
{
    private const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Tick; }

    public void KeepAwake(bool enabled)
    {
        if (SetThreadExecutionState(Continuous | (enabled ? SystemRequired : 0)) == 0)
            throw new InvalidOperationException("Windows 拒绝了系统保持运行请求。");
    }

    public TimeSpan? SessionIdleTime()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (WTSGetActiveConsoleSessionId() != (uint)process.SessionId) return null;
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input)) return null;
        uint elapsed = unchecked((uint)Environment.TickCount - input.Tick);
        return elapsed <= int.MaxValue ? TimeSpan.FromMilliseconds(elapsed) : null;
    }
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, uint length, out TokenPrivileges previous, out uint returned);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }
    public void Sleep()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法访问当前用户电源权限。");
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var p = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref p, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) || Marshal.GetLastWin32Error() == 1300) throw new Win32Exception(Marshal.GetLastWin32Error(), "当前用户没有睡眠权限；组织策略可能已限制该操作。");
            try { if (!SetSuspendState(false, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 拒绝了睡眠请求。"); }
            finally { AdjustTokenPrivileges(token, false, ref previous, 0, out _, out _); }
        }
        finally { CloseHandle(token); }
    }
}
