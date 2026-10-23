using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using AOSharp.Common.GameData;
using AOSharp.Common.Unmanaged.Imports;
using AOSharp.Core;
using AOSharp.Core.UI;
using TargetInventory = AOSharp.Core.Inventory.Inventory;

namespace Radar
{
    // One stable view tree, with managed display strings rather than retained dynels.
    // Raw queries are spread across updates; changing a value never rebuilds the scroll view.
    internal sealed class TargetInspector
    {
        private const int UnavailableStat = 1234567890;
        private const int StatsPerBatch = 32;
        private const int LiveInterval = 250, DetailInterval = 1000, SweepInterval = 2000;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly Stat[] Stats = Enum.GetValues(typeof(Stat)).Cast<Stat>()
            .Distinct().OrderBy(stat => (int)stat).ToArray();
        private static readonly Dictionary<string, Stat> NamedStats = Stats
            .ToDictionary(stat => stat.ToString(), stat => stat, StringComparer.Ordinal);
        private readonly List<Field> _summary = new List<Field>();
        private readonly List<Field> _long = new List<Field>();
        private readonly List<Field> _raw = new List<Field>();
        private readonly Dictionary<string, Field> _fields = new Dictionary<string, Field>();
        private readonly HashSet<Field> _dirty = new HashSet<Field>();
        private readonly Dictionary<string, TextView> _views = new Dictionary<string, TextView>();
        private readonly Dictionary<string, string> _displayed = new Dictionary<string, string>();
        private TextView _titleView;
        private View _content;
        private string _title = "Target: select something in AO";
        private string _displayedTitle;
        private Identity _identity = Identity.None;
        private bool _hasTarget;
        private int _statIndex;
        private long _nextLive, _nextDetails, _nextBatch, _nextSweep;

        public TargetInspector()
        {
            Summary("type", "Identity type"); Summary("instance", "Instance"); Summary("distance", "Distance");
            Summary("hp", "HP", "0x00FF7777"); Summary("nano", "Nano", "0x0077B7FF"); Summary("level", "Level / QL");
            Summary("ai", "AI level"); Summary("profession", "Profession"); Summary("breed", "Breed");
            Summary("side", "Side"); Summary("kind", "Kind"); Summary("gender", "Sex");
            Summary("title", "Title level"); Summary("org", "Clan / org stat"); Summary("team", "Team stat");
            Summary("owner", "Pet owner"); Summary("alive", "Alive"); Summary("los", "Line of sight");
            Summary("inplay", "In play"); Summary("inteam", "In my team"); Summary("moving", "Moving");
            Summary("speed", "Velocity"); Summary("runspeed", "Vehicle run speed"); Summary("accel", "Acceleration");
            Summary("radius", "Radius"); Summary("falling", "Falling"); Summary("heading", "Heading (XZ)");
            Summary("flat", "Horizontal distance"); Summary("height", "Height difference"); Summary("bearing", "Relative bearing");
            Summary("race", "Race stat"); Summary("rank", "Clan level stat"); Summary("pet", "Pet flag");
            Summary("locked", "Locked bit"); Summary("open", "Open bit"); Summary("rawprogress", "Raw scan");
            Long("name", "Name"); Long("identity", "Runtime identity (instance is not a database AOID)");
            Long("position", "Global position / local position"); Long("rotation", "Local rotation / forward");
            Long("playfield", "Current playfield"); Long("flags", "Stat flags"); Long("itemflags", "Object item flags");
            Long("fighting", "Fighting target"); Long("templates", "AOID / template candidates (named cached stats)");
            Long("effects", "Cached effects (affected target, not caster)");
            Long("equipment", "Cached equipped items"); Long("inventory", "Cached inventory / container contents");
            foreach (Stat stat in Stats)
            {
                var field = Add("stat." + ((int)stat).ToString(Invariant),
                    "[" + ((int)stat).ToString(Invariant) + "] " + stat, "0x00FFFFFF");
                _raw.Add(field);
            }
        }

        private void Summary(string key, string label, string color = "0x00FFFFFF")
        {
            Field field = Add(key, label, color);
            field.Inline = true;
            _summary.Add(field);
        }
        private void Long(string key, string label) => _long.Add(Add(key, label, "0x00FFFFFF"));
        private Field Add(string key, string label, string color)
        {
            var field = new Field { Key = key, Label = label, Color = color, Name = "TargetField" + _fields.Count,
                Value = "?", Inline = key.StartsWith("stat.", StringComparison.Ordinal) };
            _fields.Add(key, field);
            _dirty.Add(field);
            return field;
        }

        public string CreateXml(string directory)
        {
            var xml = new XmlDocument { XmlResolver = null };
            xml.Load(Path.Combine(directory, "UI", "RadarTarget.xml"));
            var content = xml.SelectSingleNode("//*[@name='TargetContent']") as XmlElement;
            if (content == null)
                throw new InvalidOperationException("Could not find the target inspector template.");
            Grid(xml, content, _summary);
            foreach (Field field in _long)
            {
                var label = Text(xml, null, field.Label, "0x0066DEEB", false);
                content.AppendChild(Row(xml, label));
                content.AppendChild(Text(xml, field.Name, "?", field.Color, true));
            }
            content.AppendChild(Row(xml, Text(xml, null,
                "Raw client stats (mode 0): ? = unavailable; 0 can also mean unset. Cached values may be incomplete.",
                "0x0066DEEB", false)));
            Grid(xml, content, _raw);
            return xml.OuterXml;
        }

        private static XmlElement Text(XmlDocument xml, string name, string value, string color, bool multiline)
        {
            var text = xml.CreateElement("TextView");
            if (name != null) text.SetAttribute("name", name);
            text.SetAttribute("value", value);
            text.SetAttribute("font", "SMALL");
            text.SetAttribute("color", color);
            text.SetAttribute("view_flags", "1");
            text.SetAttribute("feature_flags", multiline ? "TVF_RENDER_SHADOW|TVF_MULTILINE" : "TVF_RENDER_SHADOW");
            text.SetAttribute("min_size", "Point(-1,-1)");
            text.SetAttribute("max_size", "Point(-1,-1)");
            return text;
        }

        private static XmlElement Row(XmlDocument xml, XmlElement text)
        {
            var row = xml.CreateElement("View");
            row.SetAttribute("view_layout", "horizontal");
            row.SetAttribute("v_alignment", "TOP");
            row.SetAttribute("min_size", "Point(-1,13)");
            row.SetAttribute("max_size", "Point(16000,13)");
            row.AppendChild(text);
            row.AppendChild(xml.CreateElement("HLayoutSpacer"));
            return row;
        }

        private static void Grid(XmlDocument xml, XmlElement content, List<Field> fields)
        {
            var grid = xml.CreateElement("View");
            grid.SetAttribute("view_layout", "horizontal");
            grid.SetAttribute("v_alignment", "TOP");
            for (int column = 0; column < 3; column++)
            {
                var list = xml.CreateElement("View");
                list.SetAttribute("view_layout", "vertical");
                list.SetAttribute("h_alignment", "LEFT");
                list.SetAttribute("min_size", "Point(239,-1)");
                list.SetAttribute("layout_borders", column < 2 ? "Rect(0,0,12,0)" : "Rect(0,0,0,0)");
                for (int i = column; i < fields.Count; i += 3)
                {
                    Field field = fields[i];
                    list.AppendChild(Row(xml, Text(xml, field.Name, field.Label + ": ?", field.Color, false)));
                }
                grid.AppendChild(list);
            }
            content.AppendChild(grid);
        }

        public void Bind(Window window)
        {
            Unbind();
            if (!window.FindView("TargetTitle", out _titleView) || !window.FindView("TargetContent", out _content))
                throw new InvalidOperationException("Could not bind the target inspector.");
            foreach (Field field in _fields.Values)
            {
                TextView view;
                if (!window.FindView(field.Name, out view))
                    throw new InvalidOperationException("Could not bind target field " + field.Key + ".");
                _views.Add(field.Key, view);
                _dirty.Add(field);
            }
            Flush();
        }

        public void Unbind()
        {
            _views.Clear();
            _displayed.Clear();
            _titleView = null;
            _content = null;
            _displayedTitle = null;
        }

        public void Clear()
        {
            _identity = Identity.None;
            _hasTarget = false;
            _statIndex = 0;
            _nextLive = _nextDetails = _nextBatch = _nextSweep = 0;
            _title = "Target: select something in AO";
            foreach (Field field in _fields.Values) Set(field.Key, "?");
        }

        public void Update(long now, RadarRoster roster)
        {
            Dynel target = Targeting.Target;
            var local = DynelManager.LocalPlayer;
            if (Game.IsZoning || local == null || target == null || !target.IsValid)
            {
                if (_hasTarget) Clear();
                return;
            }
            Identity identity = target.Identity;
            if (!_hasTarget || identity != _identity)
            {
                Clear();
                _identity = identity;
                _hasTarget = true;
            }
            if (now >= _nextLive)
            {
                _nextLive = now + LiveInterval;
                CaptureLive(target, local, roster);
            }
            if (now >= _nextDetails)
            {
                _nextDetails = now + DetailInterval;
                CaptureDetails(target);
            }
            if (now >= _nextBatch && now >= _nextSweep)
            {
                _nextBatch = now + 16;
                int end = Math.Min(_statIndex + StatsPerBatch, Stats.Length);
                for (; _statIndex < end; _statIndex++)
                {
                    Stat stat = Stats[_statIndex];
                    Set("stat." + ((int)stat).ToString(Invariant), ReadStat(target, stat, 0));
                }
                Set("rawprogress", _statIndex.ToString(Invariant) + " / " + Stats.Length.ToString(Invariant) + " cached");
                if (_statIndex == Stats.Length)
                {
                    _statIndex = 0;
                    _nextSweep = now + SweepInterval;
                }
            }
            // Do not publish a mixed snapshot after a target disappeared or changed.
            Dynel current = Targeting.Target;
            if (!target.IsValid || current == null || !current.IsValid || current.Identity != identity)
                Clear();
        }

        // Called only by the visible presentation path; Update keeps collecting
        // into bounded managed fields while existing native views are hidden.
        public void RefreshViews() => Flush();

        private void CaptureLive(Dynel target, SimpleChar local, RadarRoster roster)
        {
            string name = Safe(() => SingleLine(target.Name));
            Set("name", name);
            Set("type", target.Identity.Type + " (" + ((int)target.Identity.Type).ToString(Invariant) + ")");
            Set("instance", NumberAndHex(target.Identity.Instance));
            Set("identity", target.Identity.Type + " / " + NumberAndHex(target.Identity.Instance));
            Set("kind", Safe(() => RadarEntity.KindOf(target).ToString()) + (target.Identity == local.Identity ? " (self)" : ""));
            Set("hp", Pool(target, "Health", "MaxHealth"));
            Set("nano", Pool(target, "CurrentNano", "MaxNanoEnergy"));
            int rosterLevel = roster.LevelOf(target.Identity);
            Profession rosterProfession = roster.ProfessionOf(target.Identity);
            Set("level", rosterLevel >= 1 ? rosterLevel.ToString(Invariant) + " (roster)" : Named(target, "Level"));
            Set("ai", Named(target, "AlienLevel"));
            Set("profession", ProfessionResolver.IsPlayerProfession(rosterProfession) ?
                rosterProfession + " (" + ((uint)rosterProfession).ToString(Invariant) + ", roster)" :
                EnumStat(target, "Profession", typeof(Profession)));
            Set("breed", EnumStat(target, "Breed", typeof(Breed)));
            Set("side", EnumStat(target, "Side", typeof(Side)));
            Set("gender", Named(target, "Sex")); Set("race", Named(target, "Race"));
            Set("title", Named(target, "TitleLevel")); Set("org", Named(target, "Clan"));
            Set("rank", Named(target, "ClanLevel")); Set("team", Named(target, "Team"));
            Set("position", Safe(() => "Global " + Vector(target.GlobalPosition) + "\nLocal  " + Vector(target.Position)));
            Set("rotation", Safe(() =>
            {
                var q = target.Rotation;
                return "Quaternion " + Float(q.X) + ", " + Float(q.Y) + ", " + Float(q.Z) + ", " + Float(q.W) +
                    "\nForward " + Vector(target.Forward);
            }));
            Set("radius", Safe(() => Float(target.Radius)));
            Set("moving", Safe(() => YesNo(target.IsMoving)));
            Set("falling", Safe(() => YesNo(target.IsFalling)));
            Set("speed", Safe(() => Float(target.Velocity)));
            Set("runspeed", Safe(() => Float(target.Runspeed)));
            Set("accel", Safe(() => Float(target.Accel)));
            Set("heading", Safe(() => Heading(target.Forward)));
            Set("flags", Safe(() =>
            {
                int flags = target.GetStat(Stat.Flags, 0);
                if (flags == UnavailableStat) return "?";
                return NumberAndHex(flags) + (target.Identity.Type == IdentityType.SimpleChar ?
                    "\n" + ((CharacterFlags)flags) : "");
            }));
            Set("playfield", Safe(() => SingleLine(Playfield.Name) + " / " + Playfield.Identity + " / model " + Playfield.ModelIdentity));
            CaptureRange(target, local);
            _title = "Target: " + name + " | " + _fields["kind"].Value + " | " + _fields["distance"].Value;
            CharacterFields(target);
            ObjectFields(target);
        }

        private void CaptureRange(Dynel target, SimpleChar local)
        {
            Set("distance", "?"); Set("flat", "?"); Set("height", "?"); Set("bearing", "?");
            try
            {
                Vector3 delta = target.GlobalPosition - local.GlobalPosition;
                if (!Finite(delta)) return;
                double flat = Math.Sqrt((double)delta.X * delta.X + (double)delta.Z * delta.Z);
                double range = Math.Sqrt(flat * flat + (double)delta.Y * delta.Y);
                Set("distance", range.ToString("0.0", Invariant) + " m");
                Set("flat", flat.ToString("0.0", Invariant) + " m");
                Set("height", delta.Y.ToString("+0.0;-0.0;0.0", Invariant) + " m");
                var forward = local.Forward;
                if (flat < 0.001 || !Finite(forward) || forward.X * forward.X + forward.Z * forward.Z < 0.000001) return;
                double angle = Math.Atan2(delta.X * forward.Z - delta.Z * forward.X,
                    delta.X * forward.X + delta.Z * forward.Z) * 180 / Math.PI;
                Set("bearing", Math.Abs(angle).ToString("0.0", Invariant) + (angle < -0.5 ? " deg left" : angle > 0.5 ? " deg right" : " deg ahead"));
            }
            catch { /* A transient unavailable pose leaves explicit unknowns. */ }
        }

        private void CharacterFields(Dynel target)
        {
            foreach (string key in new[] { "owner", "alive", "inplay", "inteam", "los", "pet", "fighting" }) Set(key, "n/a");
            if (target.Identity.Type != IdentityType.SimpleChar) return;
            var character = new SimpleChar(target);
            Set("inplay", Safe(() => YesNo(character.IsInPlay)));
            Set("inteam", Safe(() => YesNo(character.IsInTeam())));
            Set("los", Safe(() => YesNo(character.IsInLineOfSight)));
            Set("pet", Safe(() => YesNo(character.IsPet)));
            Set("alive", Safe(() =>
            {
                int hp = character.Health, maximum = character.MaxHealth;
                return hp == UnavailableStat || maximum == UnavailableStat || maximum <= 0 || hp < 0 || hp > maximum ?
                    "?" : YesNo(character.IsAlive);
            }));
            Set("fighting", Safe(() =>
            {
                var other = character.FightingTarget;
                return other == null || !other.IsValid ? "None loaded" : SingleLine(other.Name) + " " + other.Identity;
            }));
            if (!character.IsPet) return;
            Set("owner", Safe(() =>
            {
                int ownerId = character.PetOwnerId;
                if (ownerId == 0 || ownerId == UnavailableStat) return "?";
                Identity ownerIdentity = new Identity(IdentityType.SimpleChar, ownerId);
                Dynel owner = DynelManager.AllDynels.FirstOrDefault(d => d.Identity == ownerIdentity && d.IsValid);
                return owner == null ? NumberAndHex(ownerId) + " (not loaded)" : SingleLine(owner.Name) + " " + ownerIdentity;
            }));
        }

        private void ObjectFields(Dynel target)
        {
            Set("itemflags", "n/a"); Set("locked", "n/a"); Set("open", "n/a");
            IdentityType type = target.Identity.Type;
            if (type != IdentityType.Terminal && type != IdentityType.Door && type != IdentityType.Container && type != IdentityType.Corpse) return;
            Set("itemflags", Safe(() =>
            {
                var flags = new SimpleItem(target).ItemFlags;
                int bits = (int)flags;
                Set("locked", YesNo((bits & 0x40) != 0));
                Set("open", YesNo((bits & 0x80) != 0));
                return NumberAndHex(bits) + "\n" + flags;
            }));
        }

        private void CaptureDetails(Dynel target)
        {
            string[] templates = { "PrimaryTemplateID", "SecondaryItemTemplate", "ACGItemTemplateID", "ACGItemTemplateID2", "StaticType", "StaticInstance", "NPCHash" };
            Set("templates", string.Join("\n", templates.Select(name => name + ": " + Named(target, name, 0, true))));
            Set("effects", "n/a for this target kind");
            if (target.Identity.Type == IdentityType.SimpleChar)
            {
                var character = new SimpleChar(target);
                Set("effects", Safe(() =>
                {
                    if (!character.IsInPlay || !HasEffectCache(character)) return "Effect cache unavailable";
                    Buff[] buffs = character.Buffs;
                    if (buffs.Length == 0) return "No effects cached (not proof of no effects)";
                    return string.Join("\n", buffs.OrderBy(buff => buff.Name, StringComparer.OrdinalIgnoreCase).ThenBy(buff => buff.Id)
                        .Select(buff => SingleLine(buff.Name) + " [" + buff.Id.ToString(Invariant) + "] NCU " + buff.NCU.ToString(Invariant) +
                            ", line " + buff.Nanoline + ", stack " + buff.StackingOrder.ToString(Invariant) +
                            ", time " + Float(buff.RemainingTime) + " / " + Float(buff.TotalTime) + " s"));
                }));
            }
            Set("equipment", "n/a for this target kind"); Set("inventory", "n/a for this target kind");
            if (target.Identity.Type != IdentityType.SimpleChar && target.Identity.Type != IdentityType.Container && target.Identity.Type != IdentityType.Corpse) return;
            try
            {
                var items = TargetInventory.GetItems(target.Identity).OrderBy(item => item.Slot.Type).ThenBy(item => item.Slot.Instance).ToList();
                var equipment = items.Where(item => item.IsEquipped).Select(ItemLine).ToArray();
                var inventory = items.Where(item => !item.IsEquipped).Select(ItemLine).ToArray();
                Set("equipment", equipment.Length == 0 ? "No equipment cached (not proof of no equipment)" : string.Join("\n", equipment));
                Set("inventory", inventory.Length == 0 ? "No contents cached (not proof of empty)" : string.Join("\n", inventory));
            }
            catch { Set("equipment", "Cache unavailable"); Set("inventory", "Cache unavailable"); }
        }

        private static unsafe bool HasEffectCache(SimpleChar character)
        {
            // The SDK's Buffs getter dereferences this result without a null check.
            IntPtr engine = N3Engine_t.GetInstance();
            if (engine == IntPtr.Zero || !character.IsValid) return false;
            Identity identity = character.Identity;
            return N3EngineClientAnarchy_t.GetNanoTemplateInfoList(engine, &identity) != null;
        }

        private static string ItemLine(AOSharp.Core.Inventory.Item item) =>
            item.Slot.Type + "/" + item.Slot.Instance.ToString(Invariant) + " " + SingleLine(item.Name) +
            " [AOIDs " + item.Id.ToString(Invariant) + "/" + item.HighId.ToString(Invariant) +
            ", QL " + item.QualityLevel.ToString(Invariant) + ", charges " + item.Charges.ToString(Invariant) +
            ", instance " + item.UniqueIdentity + "]";

        private void Set(string key, string value)
        {
            Field field = _fields[key];
            value = value ?? "?";
            if (string.Equals(field.Value, value, StringComparison.Ordinal)) return;
            field.Value = value;
            _dirty.Add(field);
        }
        private static string Safe(Func<string> read)
        {
            try { return read() ?? "?"; }
            catch { return "?"; }
        }
        private static string ReadStat(Dynel target, Stat stat, int mode) => Safe(() =>
        {
            int value = target.GetStat(stat, mode);
            return value == UnavailableStat ? "?" : value.ToString(Invariant);
        });
        private static string Named(Dynel target, string name, int mode = 2, bool hex = false)
        {
            Stat stat;
            if (!NamedStats.TryGetValue(name, out stat)) return "n/a";
            return Safe(() =>
            {
                int value = target.GetStat(stat, mode);
                return value == UnavailableStat ? "?" : hex ? NumberAndHex(value) : value.ToString(Invariant);
            });
        }
        private static string EnumStat(Dynel target, string name, Type type)
        {
            Stat stat;
            if (!NamedStats.TryGetValue(name, out stat)) return "n/a";
            return Safe(() =>
            {
                int value = target.GetStat(stat);
                if (value == UnavailableStat) return "?";
                // Profession is uint-backed; boxed ints are rejected by IsDefined.
                object enumValue = Enum.ToObject(type, value);
                return Enum.IsDefined(type, enumValue) ?
                    Enum.GetName(type, enumValue) + " (" + value.ToString(Invariant) + ")" : value.ToString(Invariant);
            });
        }
        private static string Pool(Dynel target, string currentName, string maximumName)
        {
            Stat currentStat, maximumStat;
            if (!NamedStats.TryGetValue(currentName, out currentStat) || !NamedStats.TryGetValue(maximumName, out maximumStat)) return "n/a";
            return Safe(() =>
            {
                int current = target.GetStat(currentStat), maximum = target.GetStat(maximumStat);
                if (current == UnavailableStat || maximum == UnavailableStat || maximum <= 0 || current < 0 || current > maximum) return "?";
                return current.ToString(Invariant) + " / " + maximum.ToString(Invariant) + " (" +
                    ((double)current / maximum * 100).ToString("0.0", Invariant) + "%)";
            });
        }
        private static string Heading(Vector3 forward) => !Finite(forward) || forward.X * forward.X + forward.Z * forward.Z < 0.000001 ? "?" :
            ((Math.Atan2(forward.X, forward.Z) * 180 / Math.PI + 360) % 360).ToString("0.0", Invariant) + " deg (0=N)";
        private static bool Finite(Vector3 value) => RadarLayout.Finite(value.X) && RadarLayout.Finite(value.Y) && RadarLayout.Finite(value.Z);
        private static string Vector(Vector3 value) => Finite(value) ? Float(value.X) + ", " + Float(value.Y) + ", " + Float(value.Z) : "?";
        private static string Float(float value) => RadarLayout.Finite(value) ? value.ToString("0.00", Invariant) : "?";
        private static string YesNo(bool value) => value ? "Yes" : "No";
        private static string NumberAndHex(int value) => value.ToString(Invariant) + " / 0x" + unchecked((uint)value).ToString("X8", Invariant);
        private static string SingleLine(string value) => (value ?? "?").Replace('\r', ' ').Replace('\n', ' ');
        private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private void Flush()
        {
            if (_titleView == null || _content == null) return;
            bool changed = false;
            if (!string.Equals(_displayedTitle, _title, StringComparison.Ordinal))
            {
                _titleView.Text = Escape(_title);
                _titleView.GetPreferredSize(false);
                _displayedTitle = _title;
            }
            foreach (Field field in _dirty)
            {
                string text = field.Inline ? field.Label + ": " + field.Value : field.Value;
                string previous;
                if (_displayed.TryGetValue(field.Key, out previous) && string.Equals(previous, text, StringComparison.Ordinal)) continue;
                _views[field.Key].Text = Escape(text);
                _views[field.Key].GetPreferredSize(false);
                _displayed[field.Key] = text;
                changed = true;
            }
            _dirty.Clear();
            if (changed) _content.CalculatePreferredSize(false);
        }

        private sealed class Field
        {
            public string Key, Label, Name, Color, Value;
            public bool Inline;
        }
    }
}
