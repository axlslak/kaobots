using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace Radar
{
    internal enum RadarEditorAction { None, Edit, Lock, Reset, Cancel }

    internal sealed class RadarEditor
    {
        private const float Padding = 16, Header = 76, Footer = 76, Margin = 8;
        private const float MinimumWidth = 360, MinimumHeight = 380;
        private const uint Cyan = 0x0065DFEA, Amber = 0x00FFCF88, Error = 0x00FF8C82;
        private readonly string _xmlPath;
        private readonly RadarLayout _layout;
        private readonly NativeFrame _nativeFrame = new NativeFrame();
        private Window _window;
        private TextView _status, _size;
        private RadarEditorAction _requested;
        private bool _editing, _ready, _closing, _fadingOut, _wantOpen;
        private bool _saveFailed;
        private long _transitionStart, _motionUntil;
        private float _fadeStart;
        private string _motion = "READY", _lastStatus, _lastSize;
        private uint _lastColour;
        private Rect _frame;
        private RadarViewport _preview;
        private Vector2 _screen;

        public bool Visible => _window != null && _window.IsValid && _ready && !_closing;
        public float Opacity { get; private set; }
        public bool Moving { get; private set; }
        public bool Resizing { get; private set; }
        public Rect Frame => _frame;

        public RadarEditor(string pluginDirectory, RadarLayout layout)
        {
            _xmlPath = Path.Combine(pluginDirectory, "UI", "RadarEditor.xml");
            _layout = layout;
        }

        public void Request(RadarEditorAction action) => _requested = action;

        public RadarViewport GetViewport(Vector2 screen)
        {
            return Visible ? _preview : _layout.GetViewport(screen);
        }

        public void Update(Vector2 screen, long now)
        {
            if (_window != null && !_window.IsValid)
            {
                _window = null;
                _status = _size = null;
                _ready = _closing = _fadingOut = _editing = false;
                Opacity = 0;
            }
            if (_editing && _ready && (screen.X != _screen.X || screen.Y != _screen.Y))
            {
                if (screen.X < MinimumWidth + Margin * 2 || screen.Y < MinimumHeight + Margin * 2)
                {
                    Stop(true);
                    _screen = screen;
                    Chat.WriteLine("Radar: layout editor closed because the game window is too small. The last valid layout was saved if possible.");
                    return;
                }
                RadarViewport adjusted = RadarLayout.Fit(screen,
                    _preview.CenterX / _screen.X * screen.X, _preview.CenterY / _screen.Y * screen.Y,
                    _preview.Size / _screen.Y * screen.Y);
                SetLimits(screen);
                Place(FrameFor(adjusted, screen));
            }
            _screen = screen;

            RadarEditorAction action = _requested;
            _requested = RadarEditorAction.None;
            if (action == RadarEditorAction.Edit)
                _wantOpen = !_editing;
            else if (action == RadarEditorAction.Lock && _editing && _ready)
            {
                ReadFrame();
                if (Commit())
                    BeginClose(now);
            }
            else if (action == RadarEditorAction.Cancel)
            {
                _wantOpen = false;
                if (_editing)
                    BeginClose(now);
            }
            else if (action == RadarEditorAction.Reset)
            {
                if (_editing && _ready)
                {
                    Place(FrameFor(RadarLayout.Default(screen), screen));
                    _motion = "CENTRED";
                    _motionUntil = now + 900;
                    _saveFailed = false;
                    Moving = Resizing = false;
                }
                else if (_window == null)
                {
                    _layout.Save(RadarLayout.Default(screen), screen);
                    Chat.WriteLine("Radar: centred layout restored.");
                }
            }

            if (_window == null && _wantOpen)
                Open(screen, now);
            if (_window == null || _closing)
                return;

            if (_editing)
            {
                Rect previous = _frame;
                ReadFrame();
                bool resized = Changed(Width(previous), Width(_frame)) || Changed(Height(previous), Height(_frame));
                bool moved = !resized && (Changed(previous.MinX, _frame.MinX) || Changed(previous.MinY, _frame.MinY));
                if (moved || resized)
                {
                    _saveFailed = false;
                    Resizing = resized;
                    Moving = moved;
                    _motion = resized ? "RESIZING" : "MOVING";
                    _motionUntil = now + 220;
                }
                if (now >= _motionUntil)
                {
                    _motion = "READY";
                    Moving = Resizing = false;
                }
                SetStatus(_saveFailed ? "SAVE FAILED" : _motion, _saveFailed ? Error : now < _motionUntil ? Amber : Cyan);
                string size = Math.Round(_preview.Size).ToString(CultureInfo.InvariantCulture) +
                    " x " + Math.Round(_preview.Size).ToString(CultureInfo.InvariantCulture) + "   |   100 m";
                if (size != _lastSize)
                {
                    _size.Text = size;
                    _size.GetPreferredSize(false);
                    _lastSize = size;
                }
            }

            if (_fadingOut)
            {
                Opacity = _fadeStart * Math.Max(0, 1 - (now - _transitionStart) / 160f);
                if (Opacity <= 0)
                {
                    CloseNative();
                    return;
                }
            }
            else
                Opacity = Math.Min(1, (now - _transitionStart) / 160f);
            // Native chrome, text and controls fade together. The transparent
            // canvas contains no custom background bitmap.
            _window.SetAlpha(Opacity * 0.90f);
        }

        private void Open(Vector2 screen, long now)
        {
            _wantOpen = false;
            if (screen.X < MinimumWidth + Margin * 2 || screen.Y < MinimumHeight + Margin * 2)
                throw new InvalidOperationException("The game window is too small for the layout editor.");
            if (!File.Exists(_xmlPath))
                throw new FileNotFoundException("RadarEditor.xml must accompany this version of Radar.", _xmlPath);

            Rect wanted = FrameFor(_layout.GetViewport(screen), screen);
            _window = Window.CreateFromXml("Radar Layout", _xmlPath, wanted,
                WindowStyle.Default, WindowFlags.NoExit | WindowFlags.NoFade);
            if (_window == null)
                throw new InvalidOperationException("Could not create the radar layout editor.");
            _window.SetAlpha(0);
            _window.SetTitle("RADAR  |  DRAG THE TITLE TO MOVE");
            _nativeFrame.Reset();
            SetLimits(screen);
            // Never use the SDK's parameter-incomplete GetFrame wrapper.
            _nativeFrame.Calibrate(_window, wanted);
            SetLimits(screen);
            Place(wanted);
            Bind();
            _editing = _ready = true;
            _closing = _fadingOut = false;
            _transitionStart = now;
            _motionUntil = 0;
            _lastStatus = _lastSize = null;
            _motion = "READY";
            _saveFailed = false;
            _window.Show(true);
        }

        private void Bind()
        {
            Button save, reset, cancel;
            if (!_window.FindView("EditorStatus", out _status) || !_window.FindView("EditorSize", out _size) ||
                !_window.FindView("SaveLayout", out save) || !_window.FindView("ResetLayout", out reset) ||
                !_window.FindView("CancelLayout", out cancel))
                throw new InvalidOperationException("The radar editor controls could not be loaded.");
            save.SetColorOverride(Cyan);
            save.SetLabelColor(0x00FFFFFF);
            save.Clicked = (sender, button) => Request(RadarEditorAction.Lock);
            reset.Clicked = (sender, button) => Request(RadarEditorAction.Reset);
            cancel.Clicked = (sender, button) => Request(RadarEditorAction.Cancel);
        }

        private void SetLimits(Vector2 screen)
        {
            _nativeFrame.SetLimits(_window, MinimumWidth, MinimumHeight,
                screen.X - Margin * 2, screen.Y - Margin * 2);
        }

        private void Place(Rect frame)
        {
            _nativeFrame.Place(_window, frame);
            ReadFrame();
        }

        private void ReadFrame()
        {
            _frame = _nativeFrame.Read(_window);
            float availableWidth = Width(_frame) - Padding * 2;
            float availableHeight = Height(_frame) - Header - Footer;
            float size = Math.Min(availableWidth, availableHeight);
            if (size < 80 || !RadarLayout.Finite(size))
                throw new InvalidOperationException("The radar editor returned unusable canvas bounds.");
            _preview = new RadarViewport
            {
                X = _frame.MinX + Width(_frame) * 0.5f - size * 0.5f,
                Y = _frame.MinY + Header + availableHeight * 0.5f - size * 0.5f,
                Size = size
            };
        }

        private static Rect FrameFor(RadarViewport viewport, Vector2 screen)
        {
            float width = Math.Min(screen.X - Margin * 2, Math.Max(MinimumWidth, viewport.Size + Padding * 2));
            float height = Math.Min(screen.Y - Margin * 2, Math.Max(MinimumHeight, viewport.Size + Header + Footer));
            float x = Math.Max(Margin, Math.Min(screen.X - Margin - width, viewport.CenterX - width * 0.5f));
            float y = Math.Max(Margin, Math.Min(screen.Y - Margin - height, viewport.CenterY - height * 0.5f));
            return new Rect(x, y, x + width - 1, y + height - 1);
        }

        private bool Commit()
        {
            try
            {
                RadarViewport fitted = RadarLayout.Fit(_screen, _preview.CenterX, _preview.CenterY, _preview.Size);
                _layout.Save(fitted, _screen);
                Chat.WriteLine("Radar: layout saved and locked.");
                return true;
            }
            catch (Exception ex)
            {
                _saveFailed = true;
                SetStatus("SAVE FAILED", Error);
                Chat.WriteLine("Radar: layout was not saved; the editor is still open. " + ex.Message);
                return false;
            }
        }

        private void SetStatus(string text, uint colour)
        {
            if (text != _lastStatus)
            {
                _status.Text = text;
                _status.GetPreferredSize(false);
                _lastStatus = text;
            }
            if (colour != _lastColour)
            {
                _status.SetColor(colour);
                _lastColour = colour;
            }
        }

        private void BeginClose(long now)
        {
            _wantOpen = false;
            _editing = false;
            _fadingOut = true;
            Moving = Resizing = false;
            _fadeStart = Opacity;
            _transitionStart = now;
        }

        // Called before zoning/teardown: save the last validated snapshot, never
        // query native geometry while the game is destroying its UI.
        public void Stop(bool save)
        {
            _wantOpen = false;
            _requested = RadarEditorAction.None;
            if (save && _editing && _ready)
            {
                try { _layout.Save(RadarLayout.Fit(_screen, _preview.CenterX, _preview.CenterY, _preview.Size), _screen); }
                catch (Exception ex) { Chat.WriteLine("Radar: layout could not be saved. " + ex.Message); }
            }
            CloseNative();
        }

        private void CloseNative()
        {
            _editing = _ready = _fadingOut = false;
            Moving = Resizing = false;
            Opacity = 0;
            if (_window != null && _window.IsValid && !_closing)
            {
                _closing = true;
                _window.Close();
            }
        }

        private static bool Changed(float first, float second) => Math.Abs(first - second) > 0.25f;
        private static float Width(Rect rect) => rect.MaxX - rect.MinX + 1;
        private static float Height(Rect rect) => rect.MaxY - rect.MinY + 1;

        private sealed class NativeFrame
        {
            private bool _flag;
            private float _offsetX, _offsetY, _extraWidth, _extraHeight;

            public void Reset()
            {
                _offsetX = _offsetY = _extraWidth = _extraHeight = 0;
            }

            public void SetLimits(Window window, float minWidth, float minHeight, float maxWidth, float maxHeight)
            {
                // Limits and ResizeTo use native client extents; our validated
                // rectangle may include additional chrome. Keep both consistent.
                window.SetSizeLimits(new Vector2(Math.Max(1, minWidth - 1 - _extraWidth), Math.Max(1, minHeight - 1 - _extraHeight)),
                    new Vector2(Math.Max(1, maxWidth - 1 - _extraWidth), Math.Max(1, maxHeight - 1 - _extraHeight)));
            }

            // C++ returns Rect through a hidden buffer, then takes a bool. The
            // SDK import omits that bool; keep this correction local to Radar.
            [DllImport("GUI.dll", EntryPoint = "?GetFrame@Window@@QBE?AVRect@@_N@Z", CallingConvention = CallingConvention.ThisCall)]
            private static extern IntPtr GetFrame(IntPtr window, ref Rect result, [MarshalAs(UnmanagedType.U1)] bool flag);

            public void Calibrate(Window window, Rect wanted)
            {
                window.ResizeTo(new Vector2(Width(wanted) - 1, Height(wanted) - 1));
                window.MoveTo(wanted.MinX, wanted.MinY);
                Rect firstFalse, firstTrue, secondFalse, secondTrue;
                bool firstFalseValid = TryQuery(window, false, out firstFalse);
                bool firstTrueValid = TryQuery(window, true, out firstTrue);
                window.MoveTo(wanted.MinX + 4, wanted.MinY + 4);
                bool secondFalseValid = TryQuery(window, false, out secondFalse);
                bool secondTrueValid = TryQuery(window, true, out secondTrue);
                bool validFalse = firstFalseValid && secondFalseValid && Tracks(firstFalse, secondFalse);
                bool validTrue = firstTrueValid && secondTrueValid && Tracks(firstTrue, secondTrue);
                if (!validFalse && !validTrue)
                    throw new InvalidOperationException("This client did not provide validated window coordinates; the radar itself is unchanged.");
                _flag = validTrue && (!validFalse || Score(firstTrue, wanted) < Score(firstFalse, wanted));
                Rect first = _flag ? firstTrue : firstFalse;
                _offsetX = first.MinX - wanted.MinX;
                _offsetY = first.MinY - wanted.MinY;
                _extraWidth = Width(first) - Width(wanted);
                _extraHeight = Height(first) - Height(wanted);
                if (Math.Abs(_offsetX) > 80 || Math.Abs(_offsetY) > 80 || Math.Abs(_extraWidth) > 80 || Math.Abs(_extraHeight) > 80)
                    throw new InvalidOperationException("The radar editor's native frame mapping could not be validated.");
            }

            public void Place(Window window, Rect wanted)
            {
                window.ResizeTo(new Vector2(Width(wanted) - 1 - _extraWidth, Height(wanted) - 1 - _extraHeight));
                window.MoveTo(wanted.MinX - _offsetX, wanted.MinY - _offsetY);
                Rect actual = Read(window);
                if (Math.Abs(actual.MinX - wanted.MinX) > 3 || Math.Abs(actual.MinY - wanted.MinY) > 3 ||
                    Math.Abs(Width(actual) - Width(wanted)) > 3 || Math.Abs(Height(actual) - Height(wanted)) > 3)
                    throw new InvalidOperationException("The radar editor could not align its native frame.");
            }

            public Rect Read(Window window) => Query(window, _flag);

            private static bool TryQuery(Window window, bool flag, out Rect result)
            {
                result = default(Rect);
                try
                {
                    result = Query(window, flag);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            private static Rect Query(Window window, bool flag)
            {
                Rect result = new Rect(float.NaN, float.NaN, float.NaN, float.NaN);
                GetFrame(window.Pointer, ref result, flag);
                if (!RadarLayout.Finite(result.MinX) || !RadarLayout.Finite(result.MinY) ||
                    !RadarLayout.Finite(result.MaxX) || !RadarLayout.Finite(result.MaxY) ||
                    Width(result) < 1 || Height(result) < 1 || Width(result) > 32768 || Height(result) > 32768)
                    throw new InvalidOperationException("The native editor frame was invalid.");
                return result;
            }

            private static bool Tracks(Rect first, Rect second)
            {
                return Math.Abs(second.MinX - first.MinX - 4) <= 1 && Math.Abs(second.MinY - first.MinY - 4) <= 1 &&
                    Math.Abs(Width(second) - Width(first)) <= 1 && Math.Abs(Height(second) - Height(first)) <= 1;
            }

            private static float Score(Rect actual, Rect wanted)
            {
                return Math.Abs(actual.MinX - wanted.MinX) + Math.Abs(actual.MinY - wanted.MinY) +
                    Math.Abs(Width(actual) - Width(wanted)) + Math.Abs(Height(actual) - Height(wanted));
            }
        }
    }
}
