using Avalonia;
using LiquidGlassAvaloniaUI;
using System.Runtime.InteropServices;

namespace CodexMonitorV2;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        _mutex = new Mutex(true, "Local\\CodexMonitorV2.SingleInstance", out bool first);
        if (!first)
        {
            ActivateExisting();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseLiquidGlassPerformanceDefaults()
            .LogToTrace();

    private static void ActivateExisting()
    {
        nint hwnd = FindWindow(null, "Codex Monitor V2");
        if (hwnd == 0) return;
        ShowWindowAsync(hwnd, 9);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
