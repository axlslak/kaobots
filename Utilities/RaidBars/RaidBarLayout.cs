using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using AOSharp.Common.GameData;

namespace RaidBars
{
    // Native keys describe roster SLOTS, not character identities. Always prepare
    // all 36, including unoccupied slots; never invent members or health bars.
    internal sealed class RaidBarLayout
    {
        public const int Teams = 6, Slots = 6;
        public float Width = 168, Height = 31, ColumnGap = 3, RowGap = -1;
        public readonly float[] X = new float[Teams], Y = new float[Teams];

        public float StackHeight => Height + (Slots - 1) * (Height + RowGap);
        public float GridWidth => Width + (Teams - 1) * (Width + ColumnGap);

        public static RaidBarLayout Read(XmlDocument snapshot, Vector2 screen)
        {
            var layout = new RaidBarLayout();
            XmlElement archive = NativeRaidPreferences.Find(snapshot, "RaidBarConfig");
            float[] first = null;
            if (archive != null && archive.Name == "Archive")
            {
                for (int team = 0; team < Teams && first == null; team++)
                    for (int slot = 0; slot < Slots && first == null; slot++)
                        first = ReadFrame(FindChild(archive, "Configt" + team + "p" + slot));
            }
            if (first != null)
            {
                layout.Width = first[2] - first[0];
                layout.Height = first[3] - first[1];
            }
            var rows = new List<float>();
            var columns = new List<float>();
            for (int team = 0; team < Teams; team++)
            {
                float[] top = ReadFrame(FindChild(archive, "Configt" + team + "p0"));
                float[] next = ReadFrame(FindChild(archive, "Configt" + (team + 1) + "p0"));
                if (top != null && next != null && next[0] > top[0]) columns.Add(next[0] - top[0]);
                for (int slot = 1; slot < Slots; slot++)
                {
                    float[] previous = ReadFrame(FindChild(archive, "Configt" + team + "p" + (slot - 1)));
                    float[] frame = ReadFrame(FindChild(archive, "Configt" + team + "p" + slot));
                    if (previous != null && frame != null && frame[1] > previous[1]) rows.Add(frame[1] - previous[1]);
                }
            }
            // A median tolerates the few hand-placed/out-of-order slots in an
            // existing layout, and retains spacing chosen in an earlier setup.
            if (rows.Count != 0) layout.RowGap = Median(rows) - layout.Height;
            if (columns.Count != 0) layout.ColumnGap = Median(columns) - layout.Width;
            float[] anchor = ReadFrame(FindChild(archive, "Configt0p0")) ?? first;
            float x = anchor == null ? screen.X * 207 / 1920 : anchor[0];
            float y = anchor == null ? screen.Y * 632 / 1080 : anchor[1];
            // Reading does not change the client. On a small display, the user
            // can reduce pitch before applying rather than silently scaling art.
            layout.MakeGrid(x, y);
            for (int team = 0; team < Teams; team++)
            {
                float[] saved = ReadFrame(FindChild(archive, "Configt" + team + "p0"));
                if (saved == null) continue;
                layout.X[team] = saved[0];
                layout.Y[team] = saved[1];
            }
            return layout;
        }

        public RaidBarLayout Copy()
        {
            var copy = new RaidBarLayout { Width = Width, Height = Height, ColumnGap = ColumnGap, RowGap = RowGap };
            Array.Copy(X, copy.X, Teams);
            Array.Copy(Y, copy.Y, Teams);
            return copy;
        }

        public void MakeGrid(float x, float y)
        {
            for (int team = 0; team < Teams; team++)
            {
                X[team] = x + team * (Width + ColumnGap);
                Y[team] = y;
            }
        }

        public void Validate(Vector2 screen)
        {
            if (!Finite(Width) || !Finite(Height) || Width < 8 || Width > 4096 || Height < 4 || Height > 512 ||
                !Finite(ColumnGap) || !Finite(RowGap) || ColumnGap <= -Width || RowGap <= -Height ||
                ColumnGap > 1024 || RowGap > 512)
                throw new ArgumentException("Invalid bar size or spacing; each horizontal and vertical step must be positive.");
            for (int team = 0; team < Teams; team++)
                if (!Finite(X[team]) || !Finite(Y[team]) || X[team] < 0 || Y[team] < 0 ||
                    X[team] + Width > screen.X + 0.1f || Y[team] + StackHeight > screen.Y + 0.1f)
                    throw new ArgumentException("The six-slot team columns must fit on screen. Adjust the origin/spacing or move that team inward.");
        }

        public void Move(int team, float dx, float dy, Vector2 screen)
        {
            if (team < 0)
            {
                dx = Clamp(dx, -X.Min(), screen.X - X.Max() - Width);
                dy = Clamp(dy, -Y.Min(), screen.Y - Y.Max() - StackHeight);
                for (int i = 0; i < Teams; i++) { X[i] += dx; Y[i] += dy; }
            }
            else
            {
                X[team] = Clamp(X[team] + dx, 0, screen.X - Width);
                Y[team] = Clamp(Y[team] + dy, 0, screen.Y - StackHeight);
            }
        }

        public XmlDocument Patch(XmlDocument current, bool locked)
        {
            XmlDocument patch = NativeRaidPreferences.NewDocument();
            XmlElement source = NativeRaidPreferences.Find(current, "RaidBarConfig");
            XmlElement archive = source != null && source.Name == "Archive" ?
                (XmlElement)patch.ImportNode(source, true) : patch.CreateElement("Archive");
            archive.SetAttribute("name", "RaidBarConfig");
            archive.SetAttribute("code", "0");
            patch.DocumentElement.AppendChild(archive);
            for (int team = 0; team < Teams; team++)
                for (int slot = 0; slot < Slots; slot++)
                {
                    string key = "t" + team + "p" + slot;
                    XmlElement config = Child(archive, "Archive", "Config" + key);
                    config.SetAttribute("code", "0");
                    float x = X[team], y = Y[team] + slot * (Height + RowGap);
                    Child(config, "Rect", "WindowFrame").SetAttribute("value",
                        "Rect(" + Number(x) + "," + Number(y) + "," + Number(x + Width) + "," + Number(y + Height) + ")");
                    Child(archive, "Bool", key + "Enabled").SetAttribute("value", "true");
                }
            NativeRaidPreferences.SetLock(patch, locked);
            return patch;
        }

        public bool Matches(XmlDocument snapshot)
        {
            XmlElement archive = NativeRaidPreferences.Find(snapshot, "RaidBarConfig");
            if (archive == null || archive.Name != "Archive") return false;
            for (int team = 0; team < Teams; team++)
                for (int slot = 0; slot < Slots; slot++)
                {
                    string key = "t" + team + "p" + slot;
                    if (FindChild(archive, key + "Enabled")?.GetAttribute("value") != "true") return false;
                    float[] frame = ReadFrame(FindChild(archive, "Config" + key));
                    float x = X[team], y = Y[team] + slot * (Height + RowGap);
                    if (frame == null || Math.Abs(frame[0] - x) > 0.05 || Math.Abs(frame[1] - y) > 0.05 ||
                        Math.Abs(frame[2] - x - Width) > 0.05 || Math.Abs(frame[3] - y - Height) > 0.05) return false;
                }
            return true;
        }

        private static XmlElement Child(XmlElement parent, string tag, string name)
        {
            XmlElement element = FindChild(parent, name);
            if (element != null && element.Name != tag)
            {
                parent.RemoveChild(element);
                element = null;
            }
            if (element == null)
            {
                element = parent.OwnerDocument.CreateElement(tag);
                element.SetAttribute("name", name);
                parent.AppendChild(element);
            }
            return element;
        }

        private static XmlElement FindChild(XmlElement parent, string name) =>
            parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.GetAttribute("name") == name);

        private static float[] ReadFrame(XmlElement config)
        {
            string value = FindChild(config, "WindowFrame")?.GetAttribute("value");
            if (value == null || !value.StartsWith("Rect(", StringComparison.Ordinal) || !value.EndsWith(")", StringComparison.Ordinal)) return null;
            string[] parts = value.Substring(5, value.Length - 6).Split(',');
            if (parts.Length != 4) return null;
            var rect = new float[4];
            for (int i = 0; i < 4; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out rect[i]) || !Finite(rect[i])) return null;
            return rect[2] - rect[0] >= 8 && rect[3] - rect[1] >= 4 ? rect : null;
        }

        public static string Number(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static float Median(List<float> values)
        {
            values.Sort();
            return (values[(values.Count - 1) / 2] + values[values.Count / 2]) / 2;
        }
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}
