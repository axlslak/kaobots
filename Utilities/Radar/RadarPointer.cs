using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;

namespace Radar
{
    internal struct RadarPointerSample
    {
        public IntPtr Window;
        public Vector2 Position;
        public int ClientWidth, ClientHeight;
        public bool Inside, Down;
    }

    // Read-only Win32 polling. Native AO grips own input; this helper never
    // installs hooks, takes capture, injects input or changes DPI awareness.
    internal static class RadarPointer
    {
        private static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;

        public static bool TryRead(Vector2 screen, out RadarPointerSample sample)
        {
            sample = default(RadarPointerSample);
            IntPtr window = GetForegroundWindow();
            uint process;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out process) == 0 || process != ProcessId)
                return false;
            Point point;
            ClientRect client;
            if (!GetCursorPos(out point) || !ScreenToClient(window, ref point) || !GetClientRect(window, out client))
                return false;
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0)
                return false;
            int button = GetSystemMetrics(76) != 0 ? 2 : 1; // SM_SWAPBUTTON; logical primary.
            sample = new RadarPointerSample
            {
                Window = window,
                ClientWidth = width,
                ClientHeight = height,
                Position = new Vector2((point.X - client.Left) * screen.X / width,
                    (point.Y - client.Top) * screen.Y / height),
                Inside = point.X >= client.Left && point.X < client.Right &&
                    point.Y >= client.Top && point.Y < client.Bottom,
                Down = (GetAsyncKeyState(button) & 0x8000) != 0
            };
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    }
}
