using System.Runtime.InteropServices;

namespace WakeGlass.Core;

public sealed class SystemCapabilityService(WakeScheduler scheduler) : ICapabilityService
{
    [DllImport("powrprof.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool GetPwrCapabilities([Out] byte[] capabilities);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Saver; public uint Life, FullLife; }
    private bool? access;
    public void InvalidateAccess() => access = null;
    public Capabilities Detect()
    {
        byte[] caps = new byte[76]; // SYSTEM_POWER_CAPABILITIES uses BOOLEAN (1 byte); RTC state is at byte 64.
        if (!GetPwrCapabilities(caps)) return new(false, false, false, false, -1, false, -1, false, "", "无法读取 Windows 电源能力。");
        var statusKnown = GetSystemPowerStatus(out var status) && status.Ac != 255;
        bool ac = statusKnown && status.Ac == 1;
        int policy = -1;
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePointer) == 0)
        {
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
                var sub = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"); var setting = new Guid("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");
                uint value;
                var code = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value) : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
                if (code == 0) policy = (int)value;
            }
            finally { LocalFree(schemePointer); }
        }
        string detail = "";
        if (access == null) try { access = scheduler.ProbeAccess(); } catch (Exception ex) { access = false; detail = "计划任务权限检测：" + ex.Message; }
        int rtc = BitConverter.ToInt32(caps, 64);
        return new(caps[5] != 0, caps[20] != 0, caps[6] != 0 && caps[8] != 0, rtc >= 4 && rtc <= 5, statusKnown ? policy : -1, ac, status.Percent <= 100 ? status.Percent : -1, access == true, detail);
    }
}
