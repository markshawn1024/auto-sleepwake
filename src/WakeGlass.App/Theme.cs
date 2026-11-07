using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WakeGlass.Core;

namespace WakeGlass.App;

public static class Theme
{
    public static bool Dark { get; private set; }
    public static void Apply(AppSettings settings)
    {
        Dark = settings.Theme == "Dark" || settings.Theme == "System" && (int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) ?? 1) == 0;
        var r = Application.Current.Resources;
        r["Text"] = Brush(Dark ? "#F1F0F5" : "#23212B"); r["Muted"] = Brush(Dark ? "#ABA8B5" : "#686471");
        r["Card"] = Brush(Dark ? "#B12A292F" : "#C6FFFFFF"); r["Input"] = Brush(Dark ? "#B71F1E24" : "#D8FFFFFF");
        r["Line"] = Brush(Dark ? "#26FFFFFF" : "#17000000"); r["Accent"] = Brush(Dark ? "#C4B4F2" : "#6D559E");
        r["AccentFill"] = Brush(Dark ? "#B9A5E7" : "#756094"); r["AccentText"] = Brush(Dark ? "#211C2B" : "#FFFFFF");
        r["Hover"] = Brush(Dark ? "#25FFFFFF" : "#12000000"); r["Success"] = Brush(Dark ? "#A9C9B6" : "#32684C");
        r["Flyout"] = Brush(Dark ? "#28272E" : "#FAF9FC");
        r["WindowTint"] = Brush(Dark ? "#301B1A20" : "#D0F4F2F7");
        foreach (Window w in Application.Current.Windows) Backdrop(w, settings.Material);
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    public static void Backdrop(Window window, string material)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero) return;
        var opaque = Dark ? Color.FromRgb(30, 29, 35) : Color.FromRgb(237, 235, 240);
        var source = HwndSource.FromHwnd(handle); if (source?.CompositionTarget == null) return;
        bool transparency = (int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) ?? 1) != 0;
        if (Environment.OSVersion.Version.Build < 22621 || SystemParameters.HighContrast || !transparency || material == "Solid") { source.CompositionTarget.BackgroundColor = opaque; window.Background = new SolidColorBrush(opaque); return; }
        source.CompositionTarget.BackgroundColor = Colors.Transparent; window.Background = Brushes.Transparent;
        var margin = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }; DwmExtendFrameIntoClientArea(handle, ref margin);
        int dark = Dark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, 4);
        int type = material == "Mica" ? 2 : 3; int hr = DwmSetWindowAttribute(handle, 38, ref type, 4);
        int corner = 2; DwmSetWindowAttribute(handle, 33, ref corner, 4);
        if (hr < 0) { source.CompositionTarget.BackgroundColor = opaque; window.Background = new SolidColorBrush(opaque); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
