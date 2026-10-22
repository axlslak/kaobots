using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core.UI;

namespace RaidBars
{
    // These are SETUP grips, not raid health bars. The client continues to own
    // all health/nano rendering and targeting. Group movement ends on unload.
    internal sealed class GroupHandles : IDisposable
    {
        private sealed class Grip
        {
            public int Team;
            public Window Window;
            public Rect Rest;
        }

        private readonly List<Grip> _grips = new List<Grip>();
        private bool _armed;
        private IntPtr _inputWindow;
        private int _inputWidth, _inputHeight;
        private Vector2 _screen;
        private readonly string _windowPrefix = "RaidBars.Grip." + Guid.NewGuid().ToString("N");
        private int _sequence;

        public bool Active => _grips.Count != 0;

        public void Open(bool teams, string pluginDirectory, RaidBarLayout layout, Vector2 screen)
        {
            Dispose();
            layout.Validate(screen);
            _screen = screen;
            try
            {
                int count = teams ? RaidBarLayout.Teams : 1;
                for (int i = 0; i < count; i++)
                {
                    int team = teams ? i : -1;
                    string title = teams ? "Raid team " + (i + 1) + " - Move" : "Raid bars - Move all";
                    string name = _windowPrefix + "." + (++_sequence);
                    var window = Window.CreateFromXml(name, Path.Combine(pluginDirectory, "UI", "RaidBarsGrip.xml"),
                        new Rect(64, 64, 190, 72), WindowStyle.Default,
                        WindowFlags.NoExit | WindowFlags.NoFade | (WindowFlags)0x30);
                    if (window == null)
                    {
                        Window.GetWindowByName(name)?.Close();
                        throw new InvalidOperationException("Could not create the native move grip.");
                    }
                    // Track immediately, so a later initialization exception also
                    // closes this window rather than leaking a setup surface.
                    var grip = new Grip { Team = team, Window = window };
                    _grips.Add(grip);
                    window.SetTitle(title);
                    window.ResizeTo(new Vector2(teams ? 126 : 156, 8));
                    window.MoveTo(64, 64);
                    Rect first = Read(window);
                    window.MoveTo(68, 68);
                    Rect second = Read(window);
                    if (Math.Abs(second.MinX - first.MinX - 4) > 1 || Math.Abs(second.MinY - first.MinY - 4) > 1)
                        throw new InvalidOperationException("This client did not provide usable native grip coordinates.");
                    window.Show(true);
                }
                Place(layout);
            }
            catch { Dispose(); throw; }
        }

        public void Place(RaidBarLayout layout)
        {
            foreach (Grip grip in _grips)
            {
                if (!grip.Window.IsValid) throw new InvalidOperationException("A setup handle was closed; reopen Move all or Move teams.");
                int team = Math.Max(0, grip.Team);
                // The native title is above the tiny client body. Keep the body
                // just above the first bar; no window spans a health-bar column.
                grip.Window.MoveTo(layout.X[team], Math.Max(32, layout.Y[team] - 15));
                grip.Rest = Read(grip.Window);
            }
            _armed = false;
        }

        public bool Update(RaidBarLayout layout, Vector2 screen)
        {
            if (!Active) return false;
            if (screen.X != _screen.X || screen.Y != _screen.Y)
                throw new InvalidOperationException("Display size changed. Setup handles were stopped; reopen them on the current display.");
            EditorPointerSample pointer;
            if (!EditorPointer.TryRead(out pointer)) { _armed = false; return false; }
            if (pointer.Window != _inputWindow || pointer.ClientWidth != _inputWidth || pointer.ClientHeight != _inputHeight)
            {
                _inputWindow = pointer.Window;
                _inputWidth = pointer.ClientWidth;
                _inputHeight = pointer.ClientHeight;
                _armed = false;
            }
            if (!_armed)
            {
                // Focus loss is not release. Discard unobserved native deltas,
                // and re-arm only after a confirmed focused mouse-up.
                if (!pointer.Down) { Place(layout); _armed = true; }
                return false;
            }
            foreach (Grip grip in _grips)
            {
                if (!grip.Window.IsValid) throw new InvalidOperationException("A setup handle was closed.");
                Rect frame = Read(grip.Window);
                float dx = frame.MinX - grip.Rest.MinX, dy = frame.MinY - grip.Rest.MinY;
                if (Math.Abs(dx) < 0.25f && Math.Abs(dy) < 0.25f) continue;
                // Only actual movement accepted by the native grip counts. Do
                // not interpret cursor hover/clicks on a bar as group dragging.
                layout.Move(grip.Team, dx, dy, screen);
                grip.Rest = frame;
                if (!pointer.Down) { Place(layout); _armed = true; }
                return true;
            }
            if (!pointer.Down)
                foreach (Grip grip in _grips)
                {
                    // Correct a grip displaced against a screen clamp without
                    // accumulating that displacement into a future gesture.
                    int team = Math.Max(0, grip.Team);
                    grip.Window.MoveTo(layout.X[team], Math.Max(32, layout.Y[team] - 15));
                    grip.Rest = Read(grip.Window);
                }
            return false;
        }

        public void Dispose()
        {
            foreach (Grip grip in _grips)
                if (grip.Window.IsValid) grip.Window.Close();
            _grips.Clear();
            _armed = false;
            _inputWindow = IntPtr.Zero;
        }

        private static Rect Read(Window window)
        {
            // SDK 1.0.106 omits this bool from Window.GetFrame's import. Use the
            // complete native signature locally, as our Radar editor does.
            var frame = new Rect();
            GetFrame(window.Pointer, ref frame, false);
            if (!RaidBarLayout.Finite(frame.MinX) || !RaidBarLayout.Finite(frame.MinY) ||
                !RaidBarLayout.Finite(frame.MaxX) || !RaidBarLayout.Finite(frame.MaxY) ||
                frame.MaxX < frame.MinX || frame.MaxY < frame.MinY)
                throw new InvalidOperationException("The native grip returned invalid coordinates.");
            return frame;
        }

        [DllImport("GUI.dll", EntryPoint = "?GetFrame@Window@@QBE?AVRect@@_N@Z", CallingConvention = CallingConvention.ThisCall)]
        private static extern IntPtr GetFrame(IntPtr window, ref Rect result, [MarshalAs(UnmanagedType.U1)] bool flag);
    }
}
