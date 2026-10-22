using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace RaidBars
{
    public sealed class RaidBarsPlugin : AOPluginEntry
    {
        private const string Version = "1.0.0";
        private const string Verified = "Layout accepted in native settings. Check the visible bars; live movement is experimental.";
        private readonly Queue<Action> _requests = new Queue<Action>();
        private readonly GroupHandles _handles = new GroupHandles();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private NativeRaidPreferences _preferences;
        private RaidBarLayout _layout;
        private RaidBarLayout _preview;
        private Window _window;
        private Identity _owner;
        private bool _initialized, _initializationFailed, _disposed, _locked, _dirty;
        private long _nextTick;
        private string _status = "Read native settings, then Align 36 slots. No changes happen on load.";
        private readonly string _windowName = "RaidBars.Setup." + Guid.NewGuid().ToString("N");
        private int _windowSequence;

        public override void Run()
        {
            if (Game.IsAOLite) throw new NotSupportedException("Raid Bars needs the graphical AO client.");
            Chat.RegisterCommand("raidbars", Command);
            Game.OnUpdate += Update;
            Game.TeleportStarted += OnZoning;
            Chat.WriteLine("Raid Bars " + Version + ": /raidbars opens native raid-bar setup. Loading alone does not change your bars.");
            Enqueue(ToggleWindow);
        }

        private void Command(string command, string[] arguments, ChatWindow chat)
        {
            if (_disposed) return;
            string[] args = arguments.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.ToLowerInvariant()).ToArray();
            if (args.Length == 0) { Enqueue(ToggleWindow); return; }
            switch (args[0])
            {
                case "align": Enqueue(Align); break;
                case "read": Enqueue(ReadNative); break;
                case "lock": Enqueue(() => SetLocked(true)); break;
                case "unlock": Enqueue(() => SetLocked(false)); break;
                case "done": Enqueue(Done); break;
                case "undo": Enqueue(Undo); break;
                case "status": Enqueue(Status); break;
                case "edit":
                    if (args.Length == 2 && (args[1] == "all" || args[1] == "teams"))
                        Enqueue(() => Edit(args[1] == "teams"));
                    else Chat.WriteLine("Use /raidbars edit all or /raidbars edit teams.");
                    break;
                default:
                    Chat.WriteLine("Raid Bars: /raidbars [align|read|edit all|edit teams|lock|unlock|done|undo|status]");
                    break;
            }
        }

        private void Enqueue(Action action)
        {
            if (!_disposed && _requests.Count < 16) _requests.Enqueue(action);
        }

        private void Update(object sender, float deltaTime)
        {
            if (_disposed || Game.IsZoning || DynelManager.LocalPlayer == null) return;
            if (_initializationFailed && _requests.Count == 0) return;
            try
            {
                if (!_initialized || _owner != DynelManager.LocalPlayer.Identity) Initialize();
                if (_window != null && !_window.IsValid) { FinishEditing(); _window = null; }
                if (_requests.Count != 0) _requests.Dequeue()();
                if (!_handles.Active) return;
                if (_handles.Update(_preview, Screen())) _dirty = true;
                if (!_dirty || _clock.ElapsedMilliseconds < _nextTick) return;
                // Only while explicitly editing: import a small native archive
                // at <=7Hz. Sample the preview every frame so the final release
                // isn't lost between throttled imports.
                FlushPreview();
            }
            catch (Exception ex)
            {
                _handles.Dispose();
                _preview = null;
                _dirty = false;
                _requests.Clear();
                if (!_initialized)
                {
                    _initializationFailed = true;
                    _requests.Clear(); // Retry only on an explicit new command.
                }
                Report("Setup stopped: " + ex.Message);
            }
        }

        private void Initialize()
        {
            if (_initialized || _preferences != null)
            {
                _handles.Dispose();
                _preview = null;
                _dirty = false;
                CloseWindow();
                _requests.Clear();
            }
            // Do not bind an old character's layout to new preferences if any
            // part of initialization fails during a character transition.
            _initialized = false;
            _preferences = null;
            _layout = null;
            Identity owner = DynelManager.LocalPlayer.Identity;
            var preferences = new NativeRaidPreferences(Path.Combine(PluginDataDirectory.FullName, "Char" + owner.Instance));
            XmlDocument snapshot = preferences.Capture();
            RaidBarLayout layout = RaidBarLayout.Read(snapshot, Screen());
            _owner = owner;
            _preferences = preferences;
            _layout = layout;
            _locked = NativeRaidPreferences.Locked(snapshot);
            _initialized = true;
            _initializationFailed = false;
            _dirty = false;
        }

        private void ToggleWindow()
        {
            if (_window != null && _window.IsValid) { FinishEditing(); CloseWindow(); return; }
            string name = _windowName + "." + (++_windowSequence);
            _window = Window.CreateFromXml(name, Path.Combine(PluginDirectory, "UI", "RaidBars.xml"),
                WindowStyle.Default, WindowFlags.AutoScale | WindowFlags.NoFade);
            if (_window == null)
            {
                Window.GetWindowByName(name)?.Close();
                throw new InvalidOperationException("UI/RaidBars.xml is missing or could not be loaded.");
            }
            try
            {
                _window.SetTitle("Raid Bars " + Version + " - Native layout trial");
                Bind("Apply", Align);
                Bind("Read", ReadNative);
                Bind("MoveAll", () => Edit(false));
                Bind("MoveTeams", () => Edit(true));
                Bind("Lock", () => SetLocked(true));
                Bind("Unlock", () => SetLocked(false));
                Bind("Done", Done);
                Bind("Undo", Undo);
                Bind("StatusButton", Status);
                FillInputs();
                ShowStatus();
                _window.Show(true);
            }
            catch { CloseWindow(); throw; }
        }

        private void Bind(string name, Action action)
        {
            Button button;
            if (!_window.FindView(name, out button)) throw new InvalidOperationException("Missing Raid Bars control: " + name);
            button.Clicked = (sender, clicked) => Enqueue(action);
        }

        private void Align()
        {
            FinishEditing();
            RaidBarLayout next = _layout.Copy();
            float x = next.X[0], y = next.Y[0];
            if (_window != null && _window.IsValid)
            {
                x = Input("OriginX"); y = Input("OriginY");
                next.ColumnGap = Input("ColumnGap"); next.RowGap = Input("RowGap");
            }
            next.MakeGrid(x, y);
            Apply(next, true);
            Report(Verified);
        }

        private void Apply(RaidBarLayout next, bool locked)
        {
            next.Validate(Screen());
            _preferences.Apply(current => next.Patch(current, locked), after => next.Matches(after) && NativeRaidPreferences.Locked(after) == locked);
            _layout = next;
            _locked = locked;
            _dirty = false;
            FillInputs();
            _status = Verified + (locked ? " Native lock: ON." : " Native lock: OFF.");
            ShowStatus();
        }

        private void ReadNative()
        {
            FinishEditing();
            XmlDocument snapshot = _preferences.Capture();
            _layout = RaidBarLayout.Read(snapshot, Screen());
            _locked = NativeRaidPreferences.Locked(snapshot);
            _dirty = false;
            FillInputs();
            Report("Current client settings read. Align arranges six teams left to right, slots top to bottom.");
        }

        private void Edit(bool teams)
        {
            FinishEditing();
            // Prepare all slots first, including empty ones, and prevent native
            // individual-bar dragging from breaking the arrangement during setup.
            Apply(_layout.Copy(), true);
            _handles.Open(teams, PluginDirectory, _layout, Screen());
            _preview = _layout.Copy();
            Report(teams ? "Drag a team title to move that six-slot column. Done removes setup grips." :
                "Drag the Move all title to move all six team columns. Done removes setup grips.");
        }

        private void SetLocked(bool locked)
        {
            FinishEditing();
            XmlDocument patch = NativeRaidPreferences.NewDocument();
            NativeRaidPreferences.SetLock(patch, locked);
            _preferences.Apply(current => patch, after => NativeRaidPreferences.Locked(after) == locked);
            _locked = locked;
            Report(locked ? "Native raid bars locked. The lock remains after unloading." : "Native raid bars unlocked for individual dragging.");
        }

        private void Done()
        {
            FinishEditing();
            SetLocked(true);
            CloseWindow();
            Report("Setup finished; native lock is ON. You can unload now. Verify positions after unloading and a normal relog.");
        }

        private void Undo()
        {
            // Undo deliberately discards any uncommitted preview.
            _handles.Dispose();
            _preview = null;
            _dirty = false;
            XmlDocument snapshot = _preferences.Undo();
            _layout = RaidBarLayout.Read(snapshot, Screen());
            _locked = NativeRaidPreferences.Locked(snapshot);
            _dirty = false;
            FillInputs();
            Report("Original bar positions and enabled states restored. Newly prepared slots stay disabled. Check the visible bars.");
        }

        private void Status()
        {
            XmlDocument snapshot = _preferences.Capture();
            XmlElement archive = NativeRaidPreferences.Find(snapshot, "RaidBarConfig");
            int frames = 0, enabled = 0;
            if (archive != null && archive.Name == "Archive")
                for (int team = 0; team < 6; team++)
                    for (int slot = 0; slot < 6; slot++)
                    {
                        string key = "t" + team + "p" + slot;
                        if (archive.SelectSingleNode("Archive[@name='Config" + key + "']/Rect[@name='WindowFrame']") != null) frames++;
                        if (archive.SelectSingleNode("Bool[@name='" + key + "Enabled' and @value='true']") != null) enabled++;
                    }
            Report("Native prefs: " + frames + "/36 frames, " + enabled + "/36 enabled; lock " +
                (NativeRaidPreferences.Locked(snapshot) ? "ON" : "OFF") + ". " +
                ". Settings read-back does not prove that existing bars redrew. Snapshot: " + _preferences.SnapshotPath);
        }

        private float Input(string name)
        {
            TextInputView input;
            float result;
            if (!_window.FindView(name, out input) ||
                !float.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) || !RaidBarLayout.Finite(result))
                throw new ArgumentException("Enter a numeric " + name + " using a decimal point.");
            return result;
        }

        private void FillInputs()
        {
            if (_window == null || !_window.IsValid || _layout == null) return;
            SetInput("OriginX", _layout.X[0]); SetInput("OriginY", _layout.Y[0]);
            SetInput("ColumnGap", _layout.ColumnGap); SetInput("RowGap", _layout.RowGap);
        }

        private void SetInput(string name, float value)
        {
            TextInputView input;
            if (_window.FindView(name, out input)) input.Text = RaidBarLayout.Number(value);
        }

        private void Report(string status)
        {
            _status = status;
            Chat.WriteLine("Raid Bars: " + status);
            ShowStatus();
        }

        private void ShowStatus()
        {
            TextView status;
            if (_window != null && _window.IsValid && _window.FindView("Status", out status))
                status.Text = _status.Length <= 220 ? _status : _status.Substring(0, 217) + "...";
        }

        private static Vector2 Screen()
        {
            Vector2 screen = Window.GetScreenSize();
            if (!RaidBarLayout.Finite(screen.X) || !RaidBarLayout.Finite(screen.Y) || screen.X < 64 || screen.Y < 64)
                throw new InvalidOperationException("The native display size is unavailable.");
            return screen;
        }

        private void CloseWindow()
        {
            if (_window != null && _window.IsValid) _window.Close();
            _window = null;
        }

        public override void Teardown()
        {
            _disposed = true;
            Game.OnUpdate -= Update;
            Game.TeleportStarted -= OnZoning;
            _requests.Clear();
            try
            {
                if (_initialized && DynelManager.LocalPlayer != null && DynelManager.LocalPlayer.Identity == _owner && !Game.IsZoning)
                    FinishEditing();
            }
            catch (Exception ex) { Chat.WriteLine("Raid Bars: final setup movement could not be applied. " + ex.Message); }
            finally { _handles.Dispose(); CloseWindow(); }
            // Never restore old positions or turn off native bars on unload.
            // Every accepted edit already lives in the client's DValue store.
            // AO owns normal saving. Persistence of imported live-window
            // configuration must still be confirmed in the in-game trial.
        }

        private void FlushPreview()
        {
            if (!_dirty || _preview == null) return;
            Apply(_preview.Copy(), _locked);
            _nextTick = _clock.ElapsedMilliseconds + 150;
        }

        private void FinishEditing()
        {
            if (_handles.Active && _preview != null && _handles.Update(_preview, Screen())) _dirty = true;
            FlushPreview();
            _handles.Dispose();
            _preview = null;
        }

        private void OnZoning(object sender, EventArgs args)
        {
            // No native window geometry query once zoning has begun. Keep only
            // the last accepted import; don't carry a grip gesture into
            // another playfield or character.
            _handles.Dispose();
            if (_dirty) Report("Setup handles stopped for zoning. The last accepted layout is retained; reopen Move to continue.");
            _preview = null;
            _dirty = false;
            _requests.Clear();
            CloseWindow();
        }
    }
}
