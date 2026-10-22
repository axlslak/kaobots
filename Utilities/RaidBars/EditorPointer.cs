using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RaidBars
{
    internal struct EditorPointerSample
    {
        public IntPtr Window;
        public int ClientWidth, ClientHeight;
        public bool Down;
    }

    // Read-only Win32 polling. Native AO grips own input; this helper never
    // installs hooks, takes capture, injects input or changes DPI awareness.
    internal static class EditorPointer
    {
        private static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;

        public static bool TryRead(out EditorPointerSample sample)
        {
            sample = default(EditorPointerSample);
            IntPtr window = GetForegroundWindow();
            uint process;
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out process) == 0 || process != ProcessId)
                return false;
            ClientRect client;
            if (!GetClientRect(window, out client))
                return false;
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0)
                return false;
            int button = GetSystemMetrics(76) != 0 ? 2 : 1; // SM_SWAPBUTTON; logical primary.
            sample = new EditorPointerSample
            {
                Window = window,
                ClientWidth = width,
                ClientHeight = height,
                Down = (GetAsyncKeyState(button) & 0x8000) != 0
            };
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    }
}
