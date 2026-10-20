using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using AOSharp.Common.GameData;
using AOSharp.Common.GameData.UI;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace Radar
{
    public class RadarPlugin : AOPluginEntry
    {
        private const int RefreshMilliseconds = 250;
        private const int NoProfessionSelection = -1;
        private const float ColumnGap = 4;
        private const float ScreenMargin = 8;
        private const float ScrollbarWidth = 11;
        private static readonly Profession?[] Professions =
        {
            null, Profession.Adventurer, Profession.Agent, Profession.Bureaucrat,
            Profession.Doctor, Profession.Enforcer, Profession.Engineer, Profession.Fixer,
            Profession.Keeper, Profession.MartialArtist, Profession.Metaphysicist,
            Profession.NanoTechnician, Profession.Shade, Profession.Soldier, Profession.Trader
        };

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ProfessionResolver _professions = new ProfessionResolver();
        private readonly RadarDisplay _radar = new RadarDisplay();
        private readonly SelectedPlayerLabels _selectedLabels = new SelectedPlayerLabels();
        private readonly TextView[] _selectionMarkers = new TextView[Professions.Length];
        private readonly TextView[] _professionCounts = new TextView[Professions.Length];
        private readonly int[] _lastProfessionCounts = new int[Professions.Length];
        private List<PlayerSnapshot> _playersToShow = new List<PlayerSnapshot>();
        private HudPanel _professionPanel;
        private HudPanel _playerPanel;
        private View _professionContent;
        private View _playerContent;
        private ScrollView _professionScroll;
        private ScrollView _playerScroll;
        private Vector2 _professionSize;
        private Vector2 _playerSize;
        private bool _enabled = true;
        private bool _disposed;
        private bool _radarDrawingUnavailable;
        private bool _selectedLabelsUnavailable;
        private int _selectedProfession = NoProfessionSelection;
        private int _lastSelection = int.MinValue;
        private string _lastNames;
        private long _nextRefresh;
        private Identity _selectedPlayer = Identity.None;
        private Identity _pendingPlayer = Identity.None;
        private bool _hasPendingPlayer;

        public override void Run()
        {
            _professionPanel = CreatePanel("RadarProfessions", "RadarProfessions.xml", true, BindProfessions);
            _playerPanel = CreatePanel("RadarPlayers", "RadarPlayers.xml", true, BindPlayers);

            RefreshOverlay();
            Chat.RegisterCommand("radar", OnRadarCommand);
            Game.OnUpdate += OnUpdate;
            Game.TeleportStarted += OnTeleportStarted;
            Chat.WriteLine("Radar 1.0.11 loaded. /radar toggles; click a category to show players, again to hide them.");
        }

        private HudPanel CreatePanel(string name, string file, bool interactive, Action<Window> bind)
        {
            string path = Path.Combine(PluginDirectory, "UI", file);
            if (!File.Exists(path))
                throw new FileNotFoundException("A Radar UI file is missing.", path);

            return new HudPanel(name, path, interactive, bind);
        }

        private void BindProfessions(Window window)
        {
            if (!window.FindView("ProfessionScroll", out _professionScroll) ||
                !window.FindView("ProfessionContent", out _professionContent))
                throw new InvalidOperationException("Could not find Radar's profession column.");

            for (int i = 0; i < Professions.Length; i++)
            {
                ButtonBase button;
                if (!window.FindView("Profession" + i, out button) ||
                    !window.FindView("Selected" + i, out _selectionMarkers[i]) ||
                    !window.FindView("ProfessionCount" + i, out _professionCounts[i]))
                    throw new InvalidOperationException("Could not find a Radar profession choice.");

                // Native TextButton inherits ButtonBase; it has no button background.
                button.Tag = i;
                button.Clicked = OnProfessionClicked;
                _lastProfessionCounts[i] = -1;
            }

            _professionSize = PixelSize(_professionContent.CalculatePreferredSize(false));
            _lastSelection = int.MinValue;
        }

        private void BindPlayers(Window window)
        {
            if (!window.FindView("PlayerScroll", out _playerScroll) ||
                !window.FindView("PlayerContent", out _playerContent))
                throw new InvalidOperationException("Could not find Radar's player column.");
            for (int i = 0; i < _playersToShow.Count; i++)
            {
                ButtonBase button;
                if (!window.FindView("Player" + i, out button))
                    throw new InvalidOperationException("Could not find a Radar player choice.");
                button.Tag = _playersToShow[i].Identity;
                button.Clicked = OnPlayerClicked;
            }

            // The complete tree is loaded before it is attached to the window,
            // just like the working profession column.
            _playerSize = PixelSize(_playerContent.CalculatePreferredSize(false));
        }

        private void OnPlayerClicked(object sender, ButtonBase button)
        {
            if (_disposed || !_enabled || _selectedProfession == NoProfessionSelection)
                return;

            // Defer native UI changes until the click callback has returned.
            _pendingPlayer = (Identity)button.Tag;
            _hasPendingPlayer = true;
        }

        private void OnProfessionClicked(object sender, ButtonBase button)
        {
            if (_disposed || !_enabled)
                return;

            int profession = (int)button.Tag;
            _selectedProfession = profession == _selectedProfession ? NoProfessionSelection : profession;
            // Refresh on the next update, after the native click has finished.
            _nextRefresh = 0;
        }

        private void OnRadarCommand(string command, string[] args, ChatWindow chatWindow)
        {
            if (_disposed)
                return;

            bool enabled;
            if (args.Length == 0)
                enabled = !_enabled;
            else if (args.Length == 1 && string.Equals(args[0], "on", StringComparison.OrdinalIgnoreCase))
                enabled = true;
            else if (args.Length == 1 && string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
                enabled = false;
            else
            {
                Chat.WriteLine("Usage: /radar, /radar on, /radar off");
                return;
            }

            try
            {
                _enabled = enabled;
                _nextRefresh = 0;
                if (_enabled)
                    RefreshOverlay();
                else
                {
                    ClearPlayerSelection();
                    _professions.Clear();
                    CloseOverlay();
                }

                Chat.WriteLine(_enabled ? "Radar: on." : "Radar: off.");
            }
            catch (Exception ex)
            {
                DisableAfterError(ex);
            }
        }

        private void OnUpdate(object sender, float deltaTime)
        {
            if (!_enabled || _disposed)
                return;

            try
            {
                if (_hasPendingPlayer)
                {
                    _selectedPlayer = _pendingPlayer == _selectedPlayer ? Identity.None : _pendingPlayer;
                    _hasPendingPlayer = false;
                    _nextRefresh = 0;
                }

                if (_clock.ElapsedMilliseconds >= _nextRefresh)
                {
                    _nextRefresh = _clock.ElapsedMilliseconds + RefreshMilliseconds;
                    RefreshOverlay();
                }

                // Qubit draws on every update, independently of its roster refresh.
                DrawSelectedPlayer();
                DrawCentreRadar();
            }
            catch (Exception ex)
            {
                DisableAfterError(ex);
            }
        }

        private void RefreshOverlay()
        {
            var localPlayer = DynelManager.LocalPlayer;
            if (Game.IsZoning || localPlayer == null)
            {
                ClearPlayerSelection();
                _professions.Clear();
                CloseOverlay();
                return;
            }

            Vector2 screen = Window.GetScreenSize();
            if (screen.X <= 0 || screen.Y <= 0)
                return;

            if (!_professionPanel.EnsureCreated())
                return;

            // Keep display data separate from live dynel wrappers.
            var players = new List<PlayerSnapshot>();
            _professions.BeginRefresh();
            foreach (var player in DynelManager.Players)
            {
                if (player.Identity != localPlayer.Identity)
                    players.Add(new PlayerSnapshot(player.Identity, player.Name,
                        _professions.Observe(player.Identity, player.Profession),
                        player.Level, player.GetStat(Stat.AlienLevel)));
            }
            _professions.EndRefresh(_clock.ElapsedMilliseconds);

            RefreshProfessionCounts(players);

            bool hasSelectedProfession = _selectedProfession != NoProfessionSelection;
            Profession? profession = hasSelectedProfession ? Professions[_selectedProfession] : null;
            var visiblePlayers = players
                .Where(player => hasSelectedProfession && (!profession.HasValue || player.Profession == profession.Value))
                .OrderByDescending(player => player.Level)
                .ThenByDescending(player => player.AILevel)
                .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_selectedPlayer != Identity.None && !visiblePlayers.Any(player => player.Identity == _selectedPlayer))
                ClearPlayerSelection();
            _radar.SetPlayers(visiblePlayers.Select(player => player.Identity));

            bool playersReady = false;
            if (hasSelectedProfession)
            {
                string names = _selectedPlayer + "\n" +
                    string.Join("\n", visiblePlayers.Select(player => player.Identity + ":" + player.Label));

                if (!string.Equals(names, _lastNames, StringComparison.Ordinal))
                {
                    _playersToShow = visiblePlayers;
                    _playerPanel.ReplaceXml(CreatePlayerXml(visiblePlayers));
                    _lastNames = names;
                }
                playersReady = _playerPanel.EnsureCreated();
            }
            else
            {
                // No category is different from an empty category: do not create
                // a names window or show the usual "(none)" row.
                ClearPlayerSelection();
                _playersToShow.Clear();
                _lastNames = null;
                _playerPanel.Close();
            }

            if (_selectedProfession != _lastSelection)
            {
                for (int i = 0; i < _selectionMarkers.Length; i++)
                    _selectionMarkers[i].Text = i == _selectedProfession ? ">" : "";
                _lastSelection = _selectedProfession;
            }

            float anchorX = screen.X * 0.25f;
            float anchorY = screen.Y * 0.5f;
            ColumnLayout professionLayout = LayoutColumn(_professionPanel, _professionScroll, _professionSize,
                anchorX - ColumnGap, anchorY, anchorX - ColumnGap - ScreenMargin, screen.Y, true);

            float namesCenterY = anchorY;
            if (playersReady && !professionLayout.VerticalOverflow)
            {
                // All profession rows have the same fixed height in XML. Anchor
                // the names to the selected row, using the panel's actual top.
                float rowHeight = _professionSize.Y / Professions.Length;
                namesCenterY = professionLayout.Top + rowHeight * (_selectedProfession + 0.5f);
            }
            // In a viewport too short for the profession menu, the SDK exposes
            // no scroll offset. Keep the names on screen rather than guessing it.
            if (playersReady)
                LayoutColumn(_playerPanel, _playerScroll, _playerSize,
                    anchorX + ColumnGap, namesCenterY, screen.X - ScreenMargin - anchorX - ColumnGap, screen.Y, false);
        }

        private void RefreshProfessionCounts(List<PlayerSnapshot> players)
        {
            // Count the whole loaded roster, independently of the selected filter.
            bool changed = false;
            for (int i = 0; i < Professions.Length; i++)
            {
                int count = i == 0 ? players.Count :
                    players.Count(player => player.Profession == Professions[i].Value);
                if (count == _lastProfessionCounts[i])
                    continue;

                // These TextViews already exist when the XML is attached. Keep
                // the accepted native TextButtons and their click bindings intact.
                _professionCounts[i].Text = $" ({count})";
                _professionCounts[i].GetPreferredSize(false);
                _lastProfessionCounts[i] = count;
                changed = true;
            }
            if (changed)
                _professionSize = PixelSize(_professionContent.CalculatePreferredSize(false));
        }

        private string CreatePlayerXml(List<PlayerSnapshot> players)
        {
            // Populate the whole panel before native window creation. In 1.0.4,
            // this tree was populated only after native window attachment.
            var xml = new XmlDocument { XmlResolver = null };
            xml.Load(Path.Combine(PluginDirectory, "UI", "RadarPlayers.xml"));
            var content = xml.SelectSingleNode("//*[@name='PlayerContent']") as XmlElement;
            if (content == null)
                throw new InvalidOperationException("Could not find Radar's player-column template.");
            while (content.HasChildNodes)
                content.RemoveChild(content.FirstChild);

            if (players.Count == 0)
            {
                var empty = xml.CreateElement("TextView");
                empty.SetAttribute("value", "(none)");
                empty.SetAttribute("font", "SMALL");
                empty.SetAttribute("color", "0x00FFFFFF");
                empty.SetAttribute("feature_flags", "TVF_RENDER_SHADOW");
                content.AppendChild(empty);
            }
            for (int i = 0; i < players.Count; i++)
            {
                var button = xml.CreateElement("TextButton");
                button.SetAttribute("name", "Player" + i);
                button.SetAttribute("text", players[i].Label);
                button.SetAttribute("font", "SMALL");
                string color = players[i].Identity == _selectedPlayer ? "0x00FF0000" : "0x00FFFFFF";
                button.SetAttribute("color", color);
                button.SetAttribute("hover_color", color);
                button.SetAttribute("pressed_color", color);
                button.SetAttribute("min_size", "Point(-1,13)");
                button.SetAttribute("max_size", "Point(16000,13)");
                content.AppendChild(button);
            }
            return xml.OuterXml;
        }

        private void DrawSelectedPlayer()
        {
            if (_selectedPlayer == Identity.None || Game.IsZoning)
                return;

            var localPlayer = DynelManager.LocalPlayer;
            var player = DynelManager.Players.FirstOrDefault(candidate => candidate.Identity == _selectedPlayer);
            if (localPlayer == null || player == null || !player.IsValid)
            {
                ClearPlayerSelection();
                _nextRefresh = 0;
                return;
            }

            Vector3 position = player.GlobalPosition;
            AOSharp.Core.Debug.DrawLine(localPlayer.GlobalPosition, position, DebuggingColor.Red);
            float radius = Math.Max(1f, player.Radius);
            AOSharp.Core.Debug.DrawSphere(position + new Vector3(0, radius, 0), radius, DebuggingColor.Red);
            if (!_selectedLabelsUnavailable)
            {
                try
                {
                    _selectedLabels.Draw(player, position, radius);
                }
                catch (Exception ex)
                {
                    _selectedLabelsUnavailable = true;
                    Chat.WriteLine($"Radar: selected-player text unavailable: {ex.Message}");
                }
            }
            // These are markers, not body-material changes. Let the client render
            // them even without line of sight; depth/occlusion needs an in-game check.
        }

        private void DrawCentreRadar()
        {
            if (_radarDrawingUnavailable || Game.IsZoning)
                return;

            try
            {
                var localPlayer = DynelManager.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid)
                    return;
                _radar.Draw(Window.GetScreenSize(), localPlayer.Identity,
                    localPlayer.GlobalPosition, localPlayer.Forward, _selectedPlayer);
            }
            catch (Exception ex)
            {
                // Keep the accepted side HUD available if this client lacks
                // the additional native renderer exports.
                _radarDrawingUnavailable = true;
                Chat.WriteLine($"Radar: centre drawing unavailable: {ex.Message}");
            }
        }

        private void ClearPlayerSelection()
        {
            _selectedPlayer = Identity.None;
            _pendingPlayer = Identity.None;
            _hasPendingPlayer = false;
        }

        private struct ColumnLayout
        {
            public float Top;
            public bool VerticalOverflow;
        }

        private static ColumnLayout LayoutColumn(HudPanel panel, ScrollView scroll, Vector2 content,
            float edgeX, float centerY, float maxWidth, float screenHeight, bool growsLeft)
        {
            maxWidth = Math.Max(1, maxWidth);
            // Preserve symmetric growth about the anchor, scrolling before
            // either half of the column would run beyond the screen margins.
            float maxHeight = Math.Max(1, 2 * Math.Min(centerY - ScreenMargin,
                screenHeight - ScreenMargin - centerY));
            bool verticalOverflow = content.Y > maxHeight;
            bool horizontalOverflow = content.X + (verticalOverflow ? ScrollbarWidth : 0) > maxWidth;
            verticalOverflow |= content.Y + (horizontalOverflow ? ScrollbarWidth : 0) > maxHeight;
            horizontalOverflow |= content.X + (verticalOverflow ? ScrollbarWidth : 0) > maxWidth;

            float width = Math.Min(maxWidth, content.X + (verticalOverflow ? ScrollbarWidth : 0));
            float height = Math.Min(maxHeight, content.Y + (horizontalOverflow ? ScrollbarWidth : 0));
            float top = centerY - height * 0.5f;
            panel.Place(growsLeft ? edgeX - width : edgeX, top, width, height, scroll);
            return new ColumnLayout { Top = top, VerticalOverflow = verticalOverflow };
        }

        private static Vector2 PixelSize(Vector2 extent)
        {
            // AO's Rect/Point layout dimensions are inclusive extents.
            return new Vector2(Math.Max(1, (float)Math.Ceiling(extent.X + 1)),
                Math.Max(1, (float)Math.Ceiling(extent.Y + 1)));
        }

        private void OnTeleportStarted(object sender, EventArgs args)
        {
            _nextRefresh = 0;
            ClearPlayerSelection();
            _professions.Clear();
            CloseOverlay();
        }

        private void CloseOverlay()
        {
            _radar.Clear();
            _professionPanel?.Close();
            _playerPanel?.Close();
        }

        private void DisableAfterError(Exception ex)
        {
            _enabled = false;
            ClearPlayerSelection();
            _professions.Clear();
            CloseOverlay();
            Chat.WriteLine($"Radar disabled: {ex.Message}");
        }

        public override void Teardown()
        {
            _disposed = true;
            _enabled = false;
            ClearPlayerSelection();
            Game.OnUpdate -= OnUpdate;
            Game.TeleportStarted -= OnTeleportStarted;
            _professions.Clear();
            CloseOverlay();
        }

        private sealed class PlayerSnapshot
        {
            public readonly Identity Identity;
            public readonly string Name;
            public readonly Profession Profession;
            public readonly int Level;
            public readonly int AILevel;

            public string Label => $"{Name} ({FormatLevel(Level)}/{FormatLevel(AILevel)})";

            public PlayerSnapshot(Identity identity, string name, Profession profession, int level, int aiLevel)
            {
                Identity = identity;
                Name = name;
                Profession = profession;
                // The client uses an out-of-range sentinel for unresolved stats.
                // AI level zero is valid; unknowns sort below known values.
                Level = level >= 1 && level <= 220 ? level : -1;
                AILevel = aiLevel >= 0 && aiLevel <= 30 ? aiLevel : -1;
            }

            private static string FormatLevel(int level)
            {
                return level < 0 ? "?" : level.ToString();
            }
        }

        private sealed class HudPanel
        {
            // Native AO uses style 3 for windows without frame or background art.
            private const WindowStyle HudStyle = (WindowStyle)3;
            // Native flags 0x08/0x10/0x20 disable moving and resizing a HUD window.
            private const WindowFlags HudFlags = WindowFlags.NoExit | WindowFlags.NoFade |
                WindowFlags.AutoScale | (WindowFlags)0x38;
            private readonly string _name;
            private readonly string _path;
            private readonly bool _interactive;
            private readonly Action<Window> _bind;
            private string _xml;
            private Window _window;
            private bool _closing;
            private float _width = -1;
            private float _height = -1;
            private float _x = -1;
            private float _y = -1;

            public HudPanel(string name, string path, bool interactive, Action<Window> bind)
            {
                _name = name;
                _path = path;
                _interactive = interactive;
                _bind = bind;
            }

            public bool EnsureCreated()
            {
                if (_window != null && !_window.IsValid)
                {
                    _window = null;
                    _closing = false;
                }

                // Native close completion can arrive on a later update.
                if (_closing)
                    return false;

                if (_window == null)
                {
                    WindowFlags flags = HudFlags;
                    if (!_interactive)
                        flags |= WindowFlags.IgnoreRaycast;
                    _window = CreateWindow(flags);
                    if (_window == null)
                        throw new InvalidOperationException("Could not create " + _name + ".");
                    _width = _height = _x = _y = -1;
                    _bind(_window);
                }
                return true;
            }

            public void ReplaceXml(string xml)
            {
                // The caller compares identities as well as labels. Even identical
                // markup can need fresh click bindings for a changed identity.
                _xml = xml;
                Close();
            }

            private Window CreateWindow(WindowFlags flags)
            {
                if (_xml == null)
                    return Window.CreateFromXml(_name, _path, new Rect(0, 0, 1, 1), HudStyle, flags);

                // Each client/load owns its transient file; the native loader is synchronous.
                string path = Path.Combine(Path.GetTempPath(), "AOSharp-Radar-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    File.WriteAllText(path, _xml, new UTF8Encoding(false));
                    return Window.CreateFromXml(_name, path, new Rect(0, 0, 1, 1), HudStyle, flags);
                }
                finally
                {
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            public void Place(float x, float y, float width, float height, View viewport = null)
            {
                if (width != _width || height != _height)
                {
                    var extent = new Vector2(width - 1, height - 1);
                    viewport?.LimitMaxSize(extent);
                    _window.SetSizeLimits(extent, extent);
                    _window.ResizeTo(extent);
                    _width = width;
                    _height = height;
                }
                if (x != _x || y != _y)
                {
                    _window.MoveTo(x, y);
                    _x = x;
                    _y = y;
                }
                _window.Show(true);
            }

            public void Close()
            {
                if (_window != null && _window.IsValid && !_closing)
                {
                    _closing = true;
                    _window.Close();
                }
            }
        }
    }
}
