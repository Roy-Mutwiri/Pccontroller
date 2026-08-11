using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TradeFix.Launcher.Services;

/// <summary>Forces the native Windows title bar into dark mode so it matches this app's dark
/// theme instead of the OS-default light title bar — the same technique Windows Terminal, VS
/// Code, and the Windows 11 Settings app use. There's no public WPF API for this, only the
/// DWMWA_USE_IMMERSIVE_DARK_MODE window attribute (stable since Windows 10 20H1, despite the
/// lack of an official managed wrapper).</summary>
internal static class WindowChromeHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Safe to call before the window's handle exists yet (hooks SourceInitialized) or
    /// after (applies immediately) — callers don't need to know which.</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        if (PresentationSource.FromVisual(window) is not null)
        {
            Apply(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply(window);
        }
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int useDark = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
        {
            // pre-20H1 Windows 10 used a different attribute id for the same thing
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
        }
    }
}
