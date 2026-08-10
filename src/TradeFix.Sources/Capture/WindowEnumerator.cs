using System.Runtime.InteropServices;
using System.Text;

namespace TradeFix.Sources.Capture;

/// <summary>Lists top-level windows a user could plausibly want to capture (visible, titled,
/// non-trivial size) — the picker list for "capture this specific app" (spec section 5/19).</summary>
public static class WindowEnumerator
{
    public static IReadOnlyList<CapturableWindow> GetCapturableWindows()
    {
        var results = new List<CapturableWindow>();
        var currentProcessId = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == currentProcessId)
            {
                return true; // don't offer to capture our own Master/Agent windows
            }

            var length = GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            var title = builder.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 100 || height < 100)
            {
                return true; // skip tiny/utility windows
            }

            results.Add(new CapturableWindow(hwnd, title));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
