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
        private const int NoCategorySelection = -1;
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

        // Keep existing control IDs stable. XML inserts NPCs and Pets after
        // All and appends Others; IDs and visible row positions are distinct.
        private static readonly int NpcCategory = Professions.Length;
        private static readonly int OtherCategory = Professions.Length + 1;
        private static readonly int PetCategory = Professions.Length + 2;
        private static readonly int CategoryCount = Professions.Length + 3;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ProfessionResolver _professions = new ProfessionResolver();
        private readonly RadarDisplay _radar = new RadarDisplay();
        private readonly RadarLayout _layout = new RadarLayout();
        private RadarEditor _editor;
        private readonly SelectedPlayerLabels _selectedLabels = new SelectedPlayerLabels();
        private readonly TextView[] _selectionMarkers = new TextView[CategoryCount];
        private readonly TextView[] _professionCounts = new TextView[CategoryCount];
        private readonly int[] _lastProfessionCounts = new int[CategoryCount];
        private List<RadarEntity> _entitiesToShow = new List<RadarEntity>();
        private TextView[] _entityHealth = new TextView[0];
        private string[] _lastEntityHealth = new string[0];
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
        private int _selectedCategory = NoCategorySelection;
        private int _lastSelection = int.MinValue;
        private string _lastNames;
        private long _nextRefresh;
        private Identity _selectedEntity = Identity.None;
        private Identity _pendingEntity = Identity.None;
        private bool _hasPendingEntity;
        private Identity _observedTarget = Identity.None;
        private bool _hasObservedTarget;
        private bool _followTargetCategory;

        public override void Run()
        {
            _editor = new RadarEditor(PluginDirectory, _layout);
            _professionPanel = CreatePanel("RadarProfessions", "RadarProfessions.xml", true, BindProfessions);
            _playerPanel = CreatePanel("RadarPlayers", "RadarPlayers.xml", true, BindPlayers);

            RefreshOverlay();
            Chat.RegisterCommand("radar", OnRadarCommand);
            Game.OnUpdate += OnUpdate;
            Game.TeleportStarted += OnTeleportStarted;
            Chat.WriteLine("Radar 1.0.16 loaded. Hover near the frame for Move/Resize grips; /radar reset restores the centre.");
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

            for (int i = 0; i < CategoryCount; i++)
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
            _entityHealth = new TextView[_entitiesToShow.Count];
            _lastEntityHealth = new string[_entitiesToShow.Count];
            for (int i = 0; i < _entitiesToShow.Count; i++)
            {
                ButtonBase button;
                if (!window.FindView("Player" + i, out button))
                    throw new InvalidOperationException("Could not find a Radar player choice.");
                button.Tag = _entitiesToShow[i].Identity;
                button.Clicked = OnPlayerClicked;
                if (_entitiesToShow[i].Kind != RadarEntityKind.Player &&
                    !window.FindView("EntityHealth" + i, out _entityHealth[i]))
                    throw new InvalidOperationException("Could not find a Radar health field.");
            }

            // The complete tree is loaded before it is attached to the window,
            // just like the working profession column.
            _playerSize = PixelSize(_playerContent.CalculatePreferredSize(false));
        }

        private void OnPlayerClicked(object sender, ButtonBase button)
        {
            if (_disposed || !_enabled || _selectedCategory == NoCategorySelection)
                return;

            // Defer native UI changes until the click callback has returned.
            _pendingEntity = (Identity)button.Tag;
            _hasPendingEntity = true;
        }

        private void OnProfessionClicked(object sender, ButtonBase button)
        {
            if (_disposed || !_enabled)
                return;

            int profession = (int)button.Tag;
            _selectedCategory = profession == _selectedCategory ? NoCategorySelection : profession;
            // Browsing filters never change AO's target. Resume category follow
            // when the game target changes, keeping its marker in the meantime.
            _followTargetCategory = false;
            _pendingEntity = Identity.None;
            _hasPendingEntity = false;
            // Refresh on the next update, after the native click has finished.
            _nextRefresh = 0;
        }

        private void OnRadarCommand(string command, string[] args, ChatWindow chatWindow)
        {
            if (_disposed)
                return;

            if (args.Length == 1)
            {
                RadarEditorAction action;
                if (string.Equals(args[0], "edit", StringComparison.OrdinalIgnoreCase))
                    action = RadarEditorAction.Edit;
                else if (string.Equals(args[0], "lock", StringComparison.OrdinalIgnoreCase))
                    action = RadarEditorAction.Lock;
                else if (string.Equals(args[0], "reset", StringComparison.OrdinalIgnoreCase))
                    action = RadarEditorAction.Reset;
                else if (string.Equals(args[0], "cancel", StringComparison.OrdinalIgnoreCase))
                    action = RadarEditorAction.Cancel;
                else
                    action = RadarEditorAction.None;
                if (action != RadarEditorAction.None)
                {
                    if (action == RadarEditorAction.Edit)
                    {
                        _enabled = true;
                        _nextRefresh = 0;
                    }
                    if (_enabled)
                        _editor.Request(action);
                    return;
                }
            }

            bool enabled;
            if (args.Length == 0)
                enabled = !_enabled;
            else if (args.Length == 1 && string.Equals(args[0], "on", StringComparison.OrdinalIgnoreCase))
                enabled = true;
            else if (args.Length == 1 && string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
                enabled = false;
            else
            {
                Chat.WriteLine("Usage: /radar [on|off|edit|lock|reset|cancel]");
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
                    ClearEntitySelection();
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
                var localPlayer = DynelManager.LocalPlayer;
                if (Game.IsZoning || localPlayer == null)
                {
                    RefreshOverlay();
                    return;
                }

                UpdateEditor(localPlayer.Identity);
                UpdateTargetSelection(localPlayer.Identity);

                if (_clock.ElapsedMilliseconds >= _nextRefresh)
                {
                    _nextRefresh = _clock.ElapsedMilliseconds + RefreshMilliseconds;
                    RefreshOverlay();
                }

                // Qubit draws on every update, independently of its roster refresh.
                DrawSelectedEntity();
                DrawCentreRadar();
            }
            catch (Exception ex)
            {
                DisableAfterError(ex);
            }
        }

        private void UpdateEditor(Identity character)
        {
            Vector2 screen = Window.GetScreenSize();
            if (!RadarLayout.Finite(screen.X) || !RadarLayout.Finite(screen.Y) || screen.X <= 0 || screen.Y <= 0)
                return;
            EnsureLayout(character);
            try
            {
                _editor.Update(screen, _clock.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // A missing editor export/UI file must not take away the
                // already working radar, name lists or selected target marker.
                _editor.Stop(false);
                Chat.WriteLine("Radar: layout editor unavailable; the radar itself is unchanged. " + ex.Message);
            }
        }

        private void EnsureLayout(Identity character)
        {
            // Save to the old character's path before Load changes the owner.
            if (_layout.Character != character)
                _editor?.Stop(true);
            string warning = _layout.Load(character);
            if (warning != null)
                Chat.WriteLine(warning);
        }

        private void UpdateTargetSelection(Identity localIdentity)
        {
            // Target is the ordinary GUI selection, not SimpleChar.FightingTarget.
            // Read it every frame; the SDK's HasTarget field is not a live query.
            Dynel target = Targeting.Target;
            if (_hasPendingEntity)
            {
                Identity requested = _pendingEntity;
                _pendingEntity = Identity.None;
                _hasPendingEntity = false;
                var choice = _entitiesToShow.FirstOrDefault(entity => entity.Identity == requested);
                var loaded = DynelManager.AllDynels.FirstOrDefault(entity => entity.Identity == requested);
                if (requested != Identity.None && requested != localIdentity &&
                    choice != null && MatchesCategory(choice, _selectedCategory) &&
                    loaded != null && loaded.IsValid)
                {
                    // Idempotent selection: never clear just Radar's highlight
                    // while AO still has this target, or guess a native clear API.
                    if (target == null || target.Identity != requested)
                    {
                        Targeting.SetTarget(requested);
                        target = Targeting.Target;
                    }
                }
            }

            Identity identity = target != null && target.IsValid &&
                target.Identity != localIdentity ? target.Identity : Identity.None;
            if (_hasObservedTarget && identity == _observedTarget)
                return;

            // Only the list-click path above writes AO's target. Observing that
            // write (or a game click) cannot feed back another target request.
            _observedTarget = identity;
            _hasObservedTarget = true;
            _selectedEntity = identity;
            _followTargetCategory = identity != Identity.None;
            _nextRefresh = 0;
        }

        private void FollowTargetCategory()
        {
            if (!_followTargetCategory || _selectedEntity == Identity.None)
                return;

            var target = DynelManager.AllDynels.FirstOrDefault(entity => entity.Identity == _selectedEntity);
            if (target == null || !target.IsValid)
                return;

            RadarEntityKind kind = RadarEntity.KindOf(target);
            if (kind == RadarEntityKind.Npc)
                _selectedCategory = NpcCategory;
            else if (kind == RadarEntityKind.Pet)
                _selectedCategory = PetCategory;
            else if (kind == RadarEntityKind.Other)
                _selectedCategory = OtherCategory;
            else
            {
                Profession profession = _professions.Observe(target.Identity, new SimpleChar(target).Profession);
                int category = Array.IndexOf(Professions, (Profession?)profession);
                // Unknown professions are still visible under All. Continue
                // following until a later cache refresh supplies the real one.
                _selectedCategory = category >= 1 ? category : 0;
            }
        }

        private void RefreshOverlay()
        {
            var localPlayer = DynelManager.LocalPlayer;
            if (Game.IsZoning || localPlayer == null)
            {
                ClearEntitySelection();
                _professions.Clear();
                CloseOverlay();
                return;
            }

            Vector2 screen = Window.GetScreenSize();
            if (screen.X <= 0 || screen.Y <= 0)
                return;

            EnsureLayout(localPlayer.Identity);

            if (!_professionPanel.EnsureCreated())
                return;

            // Keep display data separate from live dynel wrappers. Only
            // players go through profession resolution and its InfoRequests.
            var entities = new List<RadarEntity>();
            var seen = new HashSet<Identity>();
            _professions.BeginRefresh();
            // Non-player details are optional during capture. Choose their
            // category first, so this very snapshot already has complete rows.
            FollowTargetCategory();
            foreach (var dynel in DynelManager.AllDynels)
            {
                if (dynel.Identity == localPlayer.Identity || !seen.Add(dynel.Identity))
                    continue;

                RadarEntityKind kind = RadarEntity.KindOf(dynel);
                Profession profession = default(Profession);
                if (kind == RadarEntityKind.Player)
                    profession = _professions.Observe(dynel.Identity, new SimpleChar(dynel).Profession);
                bool details = kind == RadarEntityKind.Player ||
                    (kind == RadarEntityKind.Npc && _selectedCategory == NpcCategory) ||
                    (kind == RadarEntityKind.Pet && _selectedCategory == PetCategory) ||
                    (kind == RadarEntityKind.Other && _selectedCategory == OtherCategory);
                entities.Add(new RadarEntity(dynel, kind, profession, details));
            }
            _professions.EndRefresh(_clock.ElapsedMilliseconds);

            RefreshProfessionCounts(entities);

            bool hasSelectedCategory = _selectedCategory != NoCategorySelection;
            var visibleEntities = entities
                .Where(entity => MatchesCategory(entity, _selectedCategory))
                .OrderByDescending(entity => entity.Level)
                .ThenByDescending(entity => entity.AILevel)
                .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entity => entity.Identity.Type)
                .ThenBy(entity => entity.Identity.Instance)
                .ToList();
            if (_selectedEntity != Identity.None && !entities.Any(entity => entity.Identity == _selectedEntity))
                ClearEntitySelection();
            _radar.SetEntities(visibleEntities.Select(entity => entity.Identity));

            bool playersReady = false;
            if (hasSelectedCategory)
            {
                string names = _selectedEntity + "\n" +
                    string.Join("\n", visibleEntities.Select(player => player.Identity + ":" + player.Label));

                // The latest snapshot shares the XML's stable identity/order
                // key, even if only HP changed during an asynchronous close.
                _entitiesToShow = visibleEntities;
                if (!string.Equals(names, _lastNames, StringComparison.Ordinal))
                {
                    _playerPanel.ReplaceXml(CreatePlayerXml(visibleEntities));
                    _lastNames = names;
                }
                playersReady = _playerPanel.EnsureCreated();
                if (playersReady)
                    RefreshEntityHealth();
            }
            else
            {
                // No category is different from an empty category: do not create
                // a names window or show the usual "(none)" row.
                _entitiesToShow.Clear();
                _lastNames = null;
                _playerPanel.Close();
            }

            if (_selectedCategory != _lastSelection)
            {
                for (int i = 0; i < _selectionMarkers.Length; i++)
                    _selectionMarkers[i].Text = i == _selectedCategory ? ">" : "";
                _lastSelection = _selectedCategory;
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
                float rowHeight = _professionSize.Y / CategoryCount;
                namesCenterY = professionLayout.Top + rowHeight * (CategoryRow(_selectedCategory) + 0.5f);
            }
            // In a viewport too short for the profession menu, the SDK exposes
            // no scroll offset. Keep the names on screen rather than guessing it.
            if (playersReady)
                LayoutColumn(_playerPanel, _playerScroll, _playerSize,
                    anchorX + ColumnGap, namesCenterY, screen.X - ScreenMargin - anchorX - ColumnGap, screen.Y, false);
        }

        private static bool MatchesCategory(RadarEntity entity, int category)
        {
            if (category == NpcCategory)
                return entity.Kind == RadarEntityKind.Npc;
            if (category == PetCategory)
                return entity.Kind == RadarEntityKind.Pet;
            if (category == OtherCategory)
                return entity.Kind == RadarEntityKind.Other;
            return category >= 0 && category < Professions.Length &&
                entity.Kind == RadarEntityKind.Player &&
                (category == 0 || entity.Profession == Professions[category].Value);
        }

        private static int CategoryRow(int category)
        {
            if (category == NpcCategory)
                return 1;
            if (category == PetCategory)
                return 2;
            if (category == OtherCategory)
                return CategoryCount - 1;
            return category == 0 ? 0 : category + 2;
        }

        private void RefreshProfessionCounts(List<RadarEntity> players)
        {
            // Count the whole loaded roster, independently of the selected filter.
            bool changed = false;
            for (int i = 0; i < CategoryCount; i++)
            {
                int count = players.Count(entity => MatchesCategory(entity, i));
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

        private void RefreshEntityHealth()
        {
            // Preloaded TextViews can change without replacing the native list,
            // preserving scrolling and click targets while a creature takes damage.
            bool changed = false;
            for (int i = 0; i < _entitiesToShow.Count; i++)
            {
                if (_entityHealth[i] == null)
                    continue;
                string health = _entitiesToShow[i].HealthLabel;
                if (string.Equals(health, _lastEntityHealth[i], StringComparison.Ordinal))
                    continue;
                _entityHealth[i].Text = health;
                _entityHealth[i].GetPreferredSize(false);
                _lastEntityHealth[i] = health;
                changed = true;
            }
            if (changed)
                _playerSize = PixelSize(_playerContent.CalculatePreferredSize(false));
        }

        private string CreatePlayerXml(List<RadarEntity> players)
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
                string color = players[i].Identity == _selectedEntity ? "0x00FF0000" : "0x00FFFFFF";
                button.SetAttribute("color", color);
                button.SetAttribute("hover_color", color);
                button.SetAttribute("pressed_color", color);
                button.SetAttribute("min_size", "Point(-1,13)");
                button.SetAttribute("max_size", "Point(16000,13)");
                if (players[i].Kind == RadarEntityKind.Player)
                    content.AppendChild(button);
                else
                {
                    var row = xml.CreateElement("View");
                    row.SetAttribute("view_layout", "horizontal");
                    row.SetAttribute("min_size", "Point(-1,13)");
                    row.SetAttribute("max_size", "Point(16000,13)");
                    row.AppendChild(button);
                    var health = xml.CreateElement("TextView");
                    health.SetAttribute("name", "EntityHealth" + i);
                    health.SetAttribute("value", players[i].HealthLabel);
                    health.SetAttribute("font", "SMALL");
                    health.SetAttribute("color", color);
                    health.SetAttribute("feature_flags", "TVF_RENDER_SHADOW");
                    health.SetAttribute("min_size", "Point(-1,13)");
                    health.SetAttribute("max_size", "Point(16000,13)");
                    row.AppendChild(health);
                    content.AppendChild(row);
                }
            }
            return xml.OuterXml;
        }

        private void DrawSelectedEntity()
        {
            if (_selectedEntity == Identity.None || Game.IsZoning)
                return;

            var localPlayer = DynelManager.LocalPlayer;
            var player = DynelManager.AllDynels.FirstOrDefault(candidate => candidate.Identity == _selectedEntity);
            if (localPlayer == null || player == null || !player.IsValid)
            {
                ClearEntitySelection();
                _nextRefresh = 0;
                return;
            }

            Vector3 position = player.GlobalPosition;
            if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z) ||
                float.IsInfinity(position.X) || float.IsInfinity(position.Y) || float.IsInfinity(position.Z))
                return;
            AOSharp.Core.Debug.DrawLine(localPlayer.GlobalPosition, position, DebuggingColor.Red);
            float radius = player.Radius;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 1f)
                radius = 1f;
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
                    Chat.WriteLine($"Radar: selected-entity text unavailable: {ex.Message}");
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
                Vector2 screen = Window.GetScreenSize();
                _radar.Draw(screen, localPlayer.Identity,
                    localPlayer.GlobalPosition, localPlayer.Forward, _selectedEntity, _editor.GetViewport(screen), _editor);
            }
            catch (Exception ex)
            {
                // Keep the accepted side HUD available if this client lacks
                // the additional native renderer exports.
                _radarDrawingUnavailable = true;
                Chat.WriteLine($"Radar: centre drawing unavailable: {ex.Message}");
            }
        }

        private void ClearEntitySelection()
        {
            _selectedEntity = Identity.None;
            _pendingEntity = Identity.None;
            _hasPendingEntity = false;
            _observedTarget = Identity.None;
            _hasObservedTarget = false;
            _followTargetCategory = false;
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
            ClearEntitySelection();
            _professions.Clear();
            CloseOverlay();
        }

        private void CloseOverlay()
        {
            _editor?.Stop(true);
            _radar.Clear();
            _professionPanel?.Close();
            _playerPanel?.Close();
        }

        private void DisableAfterError(Exception ex)
        {
            _enabled = false;
            ClearEntitySelection();
            _professions.Clear();
            CloseOverlay();
            Chat.WriteLine($"Radar disabled: {ex.Message}");
        }

        public override void Teardown()
        {
            _disposed = true;
            _enabled = false;
            ClearEntitySelection();
            Game.OnUpdate -= OnUpdate;
            Game.TeleportStarted -= OnTeleportStarted;
            _professions.Clear();
            CloseOverlay();
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
