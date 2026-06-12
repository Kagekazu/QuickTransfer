using ECommons;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace QuickTransfer;

internal static partial class CursorHoverHelpers
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hWnd, ref Point lpPoint);

    internal static bool IsMouseButtonDown(int virtualKey)
    {
        try
        {
            return GenericHelpers.IsKeyPressed(virtualKey);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetClientCursorPos(out short x, out short y)
    {
        x = 0;
        y = 0;
        try
        {
            if (!GetCursorPos(out Point p))
                return false;

            nint hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == nint.Zero)
                return false;

            if (!ScreenToClient(hwnd, ref p))
                return false;

            if (p.X < short.MinValue || p.X > short.MaxValue || p.Y < short.MinValue || p.Y > short.MaxValue)
                return false;

            x = (short)p.X;
            y = (short)p.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }
}
