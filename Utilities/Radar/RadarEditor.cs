using System;
using System.IO;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Common.Unmanaged.Imports;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace Radar
{
    internal enum RadarEditorAction { None, Edit, Lock, Reset, Cancel }

    // The frame is drawn by RadarDisplay. Native windows exist only at the two
    // labelled grips; no native window covers the radar's centre.
    internal sealed class RadarEditor
    {
        private enum GripKind { None, Move, Resize }
        private const float RevealDistance = 20, ArmDistance = 32, LeaveDistance = 40;
        private readonly RadarLayout _layout;
        private readonly NativeGrip _move, _resize;
        private RadarEditorAction _requested;
        private RadarViewport _preview, _dragOrigin;
        private Rect _nativeOrigin;
        private Vector2 _screen;
        private GripKind _active;
        private bool _hasPreview, _dirty, _armed, _suspended, _needsPlace, _recreateGrips, _unavailable;
        private int _revision, _attemptedRevision = -1;
        private long _lastTime, _awaySince, _manualUntil, _motionUntil;
        private IntPtr _inputWindow;
        private int _clientWidth, _clientHeight;

        public bool Dragging => _active != GripKind.None;
        public bool Available => !_unavailable;
        public string Caption => _layout.Caption;
        public bool Visible => _hasPreview && Opacity > 0.001f;
        public float Opacity { get; private set; }
        public bool Moving { get; private set; }
        public bool Resizing { get; private set; }
        public bool SaveFailed { get; private set; }
        public Rect Frame => FrameFor(_preview);

        public RadarEditor(string pluginDirectory, RadarLayout layout)
        {
            _layout = layout;
            string xml = Path.Combine(pluginDirectory, "UI", "RadarEditor.xml");
            _move = new NativeGrip(layout.Caption + " - Move", xml, layout.IsSquare ? 136 : 172);
            _resize = new NativeGrip(layout.Caption + " - Resize", xml, layout.IsSquare ? 104 : 172);
        }

        public void Request(RadarEditorAction action) => _requested = action;
        public RadarViewport GetViewport(Vector2 screen) => _hasPreview ? _preview : _layout.GetViewport(screen);

        public void Update(Vector2 screen, long now, bool allowHover = true)
        {
            if (_unavailable)
            {
                // Reset remains useful even if this client cannot create grips.
                RadarEditorAction action = _requested;
                _requested = RadarEditorAction.None;
                if (action == RadarEditorAction.Reset)
                {
                    _layout.Save(_layout.DefaultViewport(screen), screen);
                    Chat.WriteLine("Radar: " + Caption + " default restored.");
                }
                return;
            }
            try { UpdateCore(screen, now, allowHover); }
            catch
            {
                _unavailable = true;
                Stop(true);
                throw;
            }
        }

        private void UpdateCore(Vector2 screen, long now, bool allowHover)
        {
            float elapsed = _lastTime == 0 ? 16 : Math.Max(0, Math.Min(100, now - _lastTime));
            _lastTime = now;
            if (!_hasPreview)
            {
                _screen = screen;
                _preview = _layout.GetViewport(screen);
                _hasPreview = true;
            }
            if (screen.X != _screen.X || screen.Y != _screen.Y)
            {
                // Preserve proportions, but defer native teardown and any dirty
                // save until a focused UP confirms the gesture has ended.
                _preview = _layout.FitViewport(screen, _preview.CenterX / _screen.X * screen.X,
                    _preview.CenterY / _screen.Y * screen.Y,
                    _layout.IsSquare ? _preview.Width / _screen.Y * screen.Y : _preview.Width / _screen.X * screen.X,
                    _preview.Height / _screen.Y * screen.Y);
                _active = GripKind.None;
                _armed = false;
                _suspended = true;
                _needsPlace = true;
                _recreateGrips = true;
            }
            _screen = screen;
            _move.CheckClosed();
            _resize.CheckClosed();

            RadarPointerSample pointer;
            bool valid = RadarPointer.TryRead(screen, out pointer);
            if (valid && (_inputWindow != pointer.Window || _clientWidth != pointer.ClientWidth ||
                _clientHeight != pointer.ClientHeight))
            {
                _inputWindow = pointer.Window;
                _clientWidth = pointer.ClientWidth;
                _clientHeight = pointer.ClientHeight;
                _armed = false;
                _suspended = true;
                _recreateGrips = true;
            }

            if (!valid)
            {
                // Focus/query loss is not a mouse-up. Keep native drag ownership;
                // freeze the preview and wait for a focused UP before re-arming.
                _armed = false;
                _suspended = true;
            }
            else if (!pointer.Down)
            {
                if (_suspended)
                {
                    _active = GripKind.None;
                    _needsPlace = true;
                    TrySave(false); // Last validated snapshot, no ambiguous delta.
                    _suspended = false;
                }
                else if (_armed && !_needsPlace)
                    ObserveNativeMovement(now);
                if (_active != GripKind.None)
                    FinishGesture(now);
                if (_recreateGrips)
                {
                    _move.Close();
                    _resize.Close();
                    _recreateGrips = false;
                }
                _armed = true;
                HandleRequest(now);
            }
            else if (_armed && !_suspended && !_needsPlace)
                ObserveNativeMovement(now);

            Rect moveRect = GripRect(GripKind.Move), resizeRect = GripRect(GripKind.Resize);
            float hotDistance = valid && pointer.Inside ? Math.Min(
                DistanceToRect(pointer.Position, NativeGrip.Footprint(moveRect)),
                DistanceToRect(pointer.Position, NativeGrip.Footprint(resizeRect))) : float.MaxValue;
            float distance = valid && pointer.Inside ?
                Math.Min(DistanceToPerimeter(pointer.Position, Frame), hotDistance) : float.MaxValue;
            bool manual = now < _manualUntil;
            bool dragging = _active != GripKind.None && valid && !_suspended;

            if ((allowHover || manual) && valid && !pointer.Down && _armed && screen.X >= 240 && screen.Y >= 240 &&
                (distance < ArmDistance || manual))
            {
                // Arm before the pointer reaches the actual grip. Never create,
                // close or reposition an ordinary grip in the middle of a press.
                bool first = _move.Ensure(moveRect), second = _resize.Ensure(resizeRect);
                if (first && second && _needsPlace)
                {
                    _move.Place(moveRect);
                    _resize.Place(resizeRect);
                    _needsPlace = false;
                }
            }

            if (!allowHover && !manual && !dragging && valid && !pointer.Down)
            {
                _move.Close();
                _resize.Close();
            }
            float proximity = allowHover ? Smooth(Clamp(1 - distance / RevealDistance, 0, 1)) : 0;
            float target = dragging || manual ? 1 : valid ? proximity : 0;
            if (SaveFailed && valid)
                target = Math.Max(target, 0.45f);
            Opacity += (target - Opacity) * (1 - (float)Math.Exp(-elapsed / (target > Opacity ? 85 : 145)));
            if (Opacity < 0.001f && target == 0)
                Opacity = 0;
            if (1 - Opacity < 0.001f && target == 1)
                Opacity = 1;

            // The two small native captions fade; line chrome grows continuously
            // with proximity because Add2DLine has RGB, not an alpha channel.
            _move.SetAlpha(_active == GripKind.Resize ? 0 : Opacity * 0.92f);
            _resize.SetAlpha(_active == GripKind.Move ? 0 : Opacity * 0.92f);

            if (valid && !pointer.Down && !dragging && !manual && distance > LeaveDistance)
            {
                if (_awaySince == 0)
                    _awaySince = now;
                if (now - _awaySince >= 240)
                {
                    TrySave(false);
                    if (Opacity == 0)
                    {
                        _move.Close();
                        _resize.Close();
                    }
                }
            }
            else
                _awaySince = 0;

            if (now >= _motionUntil && _active == GripKind.None)
                Moving = Resizing = false;
        }

        private void HandleRequest(long now)
        {
            RadarEditorAction action = _requested;
            _requested = RadarEditorAction.None;
            if (action == RadarEditorAction.Edit)
                _manualUntil = now + 3000; // Optional discovery aid; not an edit mode.
            else if (action == RadarEditorAction.Lock)
            {
                TrySave(true);
                _manualUntil = 0;
            }
            else if (action == RadarEditorAction.Cancel)
            {
                _preview = _layout.GetViewport(_screen);
                _dirty = SaveFailed = false;
                _needsPlace = true;
                _manualUntil = 0;
                _revision++;
            }
            else if (action == RadarEditorAction.Reset)
            {
                SetPreview(_layout.DefaultViewport(_screen));
                // Reset must replace saved geometry even if screen clamping
                // already makes a custom layout look like this default.
                _dirty = true;
                _revision++;
                _needsPlace = true;
                TrySave(true);
                _manualUntil = now + 1600;
                Chat.WriteLine("Radar: " + Caption + (SaveFailed ? " default could not be saved." : " default restored."));
            }
        }

        private void ObserveNativeMovement(long now)
        {
            if (_active != GripKind.None && !ActiveGrip.Ready)
            {
                // An externally removed grip is an interruption, not proof of
                // release. Save the validated snapshot on a later focused UP.
                _active = GripKind.None;
                _armed = false;
                _suspended = true;
                _needsPlace = true;
                return;
            }
            if (_active == GripKind.None)
            {
                Rect current;
                if (_move.Displaced(out current))
                    BeginGesture(GripKind.Move, current);
                else if (_resize.Displaced(out current))
                    BeginGesture(GripKind.Resize, current);
            }
            else
                ApplyNative(ActiveGrip.Read());

            if (_active != GripKind.None)
            {
                Moving = _active == GripKind.Move;
                Resizing = _active == GripKind.Resize;
                _motionUntil = now + 220;
            }
        }

        private NativeGrip ActiveGrip => _active == GripKind.Move ? _move : _resize;

        private void BeginGesture(GripKind kind, Rect current)
        {
            // Actual native displacement proves AO accepted the gesture. Cursor
            // proximity alone never grants us ownership of a game click.
            _active = kind;
            _nativeOrigin = ActiveGrip.RestFrame;
            _dragOrigin = _preview;
            SaveFailed = false;
            ApplyNative(current);
        }

        private void ApplyNative(Rect current)
        {
            float dx = current.MinX - _nativeOrigin.MinX, dy = current.MinY - _nativeOrigin.MinY;
            RadarViewport next;
            if (_active == GripKind.Move)
                next = _layout.FitViewport(_screen, _dragOrigin.CenterX + dx, _dragOrigin.CenterY + dy, _dragOrigin.Width, _dragOrigin.Height);
            else if (_layout.IsSquare)
            {
                // Project a corner drag onto a square's diagonal. Keep its upper
                // left anchor and the radar circular; the outer ring stays100m.
                float maximum = Math.Max(1, Math.Min(_screen.X - 8 - _dragOrigin.X, _screen.Y - 8 - _dragOrigin.Y));
                float size = Clamp(_dragOrigin.Size + (dx + dy) * 0.5f, Math.Min(160, maximum), maximum);
                next = new RadarViewport { X = _dragOrigin.X, Y = _dragOrigin.Y, Size = size };
            }
            else
            {
                float maxWidth = Math.Max(1, _screen.X - _layout.SideMargin - _dragOrigin.X);
                float maxHeight = Math.Max(1, _screen.Y - _layout.BottomMargin - _dragOrigin.Y);
                next = new RadarViewport { X = _dragOrigin.X, Y = _dragOrigin.Y,
                    Width = Clamp(_dragOrigin.Width + dx, Math.Min(180, maxWidth), maxWidth),
                    Height = Clamp(_dragOrigin.Height + dy, Math.Min(64, maxHeight), maxHeight) };
            }
            SetPreview(next);
        }

        private void SetPreview(RadarViewport next)
        {
            if (Math.Abs(next.X - _preview.X) <= 0.05f && Math.Abs(next.Y - _preview.Y) <= 0.05f &&
                Math.Abs(next.Width - _preview.Width) <= 0.05f && Math.Abs(next.Height - _preview.Height) <= 0.05f)
                return;
            _preview = next;
            _dirty = true;
            _revision++;
        }

        private void FinishGesture(long now)
        {
            _active = GripKind.None;
            _motionUntil = now + 220;
            _needsPlace = true;
            TrySave(false);
        }

        private void TrySave(bool force)
        {
            if (!_hasPreview || !_dirty || (!force && _attemptedRevision == _revision))
                return;
            _attemptedRevision = _revision;
            try
            {
                _layout.Save(_preview, _screen);
                _dirty = SaveFailed = false;
            }
            catch (Exception ex)
            {
                SaveFailed = true;
                Chat.WriteLine("Radar: " + Caption + " layout could not be saved. The preview is retained; /radar lock retries, /radar cancel restores the saved layout. " + ex.Message);
            }
        }

        public void Hide()
        {
            // Hiding is reversible. Retry a dirty save, retaining the validated
            // preview/error if it fails instead of discarding the user's layout.
            TrySave(true);
            _move.Hide();
            _resize.Hide();
            _move.Close();
            _resize.Close();
            _active = GripKind.None;
            _armed = _suspended = false;
            _recreateGrips = false;
            _lastTime = _awaySince = _manualUntil = _motionUntil = 0;
            _inputWindow = IntPtr.Zero;
            _needsPlace = true;
            Moving = Resizing = false;
            Opacity = 0;
        }

        public void Stop(bool save)
        {
            if (save)
                TrySave(false);
            _move.Close();
            _resize.Close();
            _active = GripKind.None;
            _hasPreview = _armed = _suspended = _dirty = SaveFailed = false;
            _recreateGrips = false;
            _requested = RadarEditorAction.None;
            _lastTime = _awaySince = _manualUntil = _motionUntil = 0;
            _inputWindow = IntPtr.Zero;
            _needsPlace = true;
            Moving = Resizing = false;
            Opacity = 0;
        }

        private Rect GripRect(GripKind kind) => GripRect(kind, _preview, _screen);

        private Rect GripRect(GripKind kind, RadarViewport viewport, Vector2 screen)
        {
            Rect frame = FrameFor(viewport);
            NativeGrip grip = kind == GripKind.Move ? _move : _resize;
            float x = kind == GripKind.Move ? (frame.MinX + frame.MaxX + 1 - grip.Width) * 0.5f :
                frame.MaxX - 4 - grip.Width;
            float y = kind == GripKind.Move ? frame.MinY + 26 : frame.MaxY - 4 - NativeGrip.Height;
            // These are calibrated client coordinates. Leave room for the
            // original style0 native title/tab above them, including at edges.
            x = Clamp(x, 8, screen.X - grip.Width - 8);
            y = Clamp(y, 32, screen.Y - NativeGrip.Height - 8);
            return new Rect(x, y, x + grip.Width - 1, y + NativeGrip.Height - 1);
        }

        private Rect FrameFor(RadarViewport viewport) => new Rect(
            viewport.X - 12, viewport.Y - 44, viewport.X + viewport.Width + 11,
            viewport.Y + viewport.Height + (_layout.IsSquare ? 11 : 43));

        public float HoverDistance(Vector2 point, Vector2 screen)
        {
            RadarViewport viewport = GetViewport(screen);
            return Math.Min(DistanceToPerimeter(point, FrameFor(viewport)), Math.Min(
                DistanceToRect(point, NativeGrip.Footprint(GripRect(GripKind.Move, viewport, screen))),
                DistanceToRect(point, NativeGrip.Footprint(GripRect(GripKind.Resize, viewport, screen)))));
        }

        private static float DistanceToRect(Vector2 point, Rect rect)
        {
            float x = Math.Max(rect.MinX - point.X, Math.Max(0, point.X - rect.MaxX));
            float y = Math.Max(rect.MinY - point.Y, Math.Max(0, point.Y - rect.MaxY));
            return (float)Math.Sqrt(x * x + y * y);
        }

        private static float DistanceToPerimeter(Vector2 point, Rect rect)
        {
            if (point.X < rect.MinX || point.X > rect.MaxX || point.Y < rect.MinY || point.Y > rect.MaxY)
                return DistanceToRect(point, rect);
            return Math.Min(Math.Min(point.X - rect.MinX, rect.MaxX - point.X),
                Math.Min(point.Y - rect.MinY, rect.MaxY - point.Y));
        }

        private static float Smooth(float value) => value * value * (3 - 2 * value);
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        private static float Width(Rect rect) => rect.MaxX - rect.MinX + 1;
        private static float Height(Rect rect) => rect.MaxY - rect.MinY + 1;

        private sealed class NativeGrip
        {
            public const float Height = 8;
            public readonly float Width;
            public Rect RestFrame { get; private set; }
            private readonly string _name, _xml;
            private readonly NativeFrame _geometry = new NativeFrame();
            private Window _window;
            private bool _ready, _closing;
            public bool Ready => _ready && !_closing && _window != null && _window.IsValid;

            public NativeGrip(string name, string xml, float width)
            {
                _name = name;
                _xml = xml;
                Width = width;
            }

            public void CheckClosed()
            {
                if (_window != null && !_window.IsValid)
                {
                    _window = null;
                    _ready = _closing = false;
                }
            }

            public bool Ensure(Rect wanted)
            {
                CheckClosed();
                if (_window != null)
                    return _ready && !_closing;
                if (!File.Exists(_xml))
                    throw new FileNotFoundException("The updated UI/RadarEditor.xml must accompany Radar1.0.17.", _xml);
                // Both are move-only native windows. A corner proxy's MOVE is
                // interpreted as resize; it is never a miniature resizable UI.
                _window = Window.CreateFromXml(_name, _xml, wanted, WindowStyle.Default,
                    WindowFlags.NoExit | WindowFlags.NoFade | (WindowFlags)0x30);
                if (_window == null)
                    throw new InvalidOperationException("Could not create a radar grip.");
                _window.SetAlpha(0);
                View canvas;
                if (!_window.FindView("RadarGripCanvas", out canvas))
                    throw new InvalidOperationException("Radar requires the updated, minimal RadarEditor.xml.");
                _geometry.Reset();
                // Calibrate away from screen edges so the +4 probe is not clamped.
                _geometry.Calibrate(_window, new Rect(64, 64, 64 + Width - 1, 64 + Height - 1));
                _geometry.SetLimits(_window, Width, Height, Width, Height);
                _ready = true;
                Place(wanted);
                _window.Show(true);
                Place(wanted);
                return true;
            }

            public void Place(Rect wanted)
            {
                if (!_ready || _closing || _window == null || !_window.IsValid)
                    return;
                _geometry.Place(_window, wanted);
                RestFrame = _geometry.Read(_window);
            }

            public Rect Read() => _geometry.Read(_window);

            public bool Displaced(out Rect current)
            {
                current = default(Rect);
                if (!_ready || _closing || _window == null || !_window.IsValid)
                    return false;
                current = Read();
                return Math.Abs(current.MinX - RestFrame.MinX) > 0.25f ||
                    Math.Abs(current.MinY - RestFrame.MinY) > 0.25f;
            }

            public void SetAlpha(float alpha)
            {
                if (_ready && !_closing && _window != null && _window.IsValid)
                    _window.SetAlpha(alpha);
            }

            public void Hide()
            {
                if (_window != null && _window.IsValid)
                    Window_c.Show(_window.Pointer, false);
            }

            public void Close()
            {
                _ready = false;
                if (_window != null && _window.IsValid && !_closing)
                {
                    _closing = true;
                    _window.Close();
                }
            }

            // Original style0/tab insets from native RE. Gesture ownership does
            // not depend on these estimates; observed native movement is required.
            public static Rect Footprint(Rect client) => new Rect(
                client.MinX - 5, client.MinY - 26, client.MaxX + 5, client.MaxY + 5);
        }

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
                float width = Width(wanted) - 1 - _extraWidth, height = Height(wanted) - 1 - _extraHeight;
                if (width < 1 || height < 1)
                    throw new InvalidOperationException("This client cannot create a grip with usable client dimensions.");
                window.ResizeTo(new Vector2(width, height));
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
