using System;
using System.Globalization;
using System.IO;
using System.Xml;
using AOSharp.Common.GameData;

namespace Radar
{
    internal enum RadarPanelKind { Radar, Categories, Entities }

    internal struct RadarViewport
    {
        public float X, Y, Width, Height;
        public float Size { get => Width; set { Width = Height = value; } }
        public float CenterX => X + Width * 0.5f;
        public float CenterY => Y + Height * 0.5f;
    }

    // Each panel owns its file; saving one cannot overwrite another's geometry.
    internal sealed class RadarLayout
    {
        private const float Margin = 8;
        public readonly RadarPanelKind Kind;
        public bool IsSquare => Kind == RadarPanelKind.Radar;
        public float SideMargin => IsSquare ? Margin : 16;
        public float BottomMargin => IsSquare ? Margin : 52;
        public string Caption => Kind.ToString();
        public Identity Character => _character;
        private float _centerX, _centerY, _width, _height;
        private bool _saved;
        private string _path;
        private Identity _character = Identity.None;

        public RadarLayout(RadarPanelKind kind = RadarPanelKind.Radar) { Kind = kind; }

        public string Load(Identity character)
        {
            if (_character == character)
                return null;
            _character = character;
            _saved = false;
            string file = IsSquare ? "layout.xml" : Kind == RadarPanelKind.Categories ? "categories.xml" : "entities.xml";
            _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AOSharp", "Radar", character.Instance.ToString("X8", CultureInfo.InvariantCulture), file);
            try
            {
                if (!File.Exists(_path))
                    return null;
                var xml = new XmlDocument { XmlResolver = null };
                xml.Load(_path);
                XmlElement root = xml.DocumentElement;
                float x, y, width, height;
                if (root == null || root.Name != "RadarLayout" ||
                    !Read(root, "centerX", out x) || !Read(root, "centerY", out y) || x < 0 || x > 1 || y < 0 || y > 1)
                    throw new InvalidDataException("Invalid saved panel layout.");
                if (IsSquare)
                {
                    // Keep the accepted radar file/schema and height-based scale.
                    if (root.GetAttribute("version") != "1" || !Read(root, "size", out height))
                        throw new InvalidDataException("Invalid saved radar size.");
                    width = height;
                }
                else if (root.GetAttribute("version") != "2" || root.GetAttribute("panel") != Caption ||
                    !Read(root, "width", out width) || !Read(root, "height", out height))
                    throw new InvalidDataException("Invalid saved panel dimensions.");
                if (width <= 0 || width > 2 || height <= 0 || height > 2)
                    throw new InvalidDataException("Saved panel dimensions are out of range.");
                _centerX = x; _centerY = y; _width = width; _height = height;
                _saved = true;
                return null;
            }
            catch (Exception ex)
            {
                return "Radar: " + Caption + " layout could not be read; using its default. " + ex.Message;
            }
        }

        public RadarViewport GetViewport(Vector2 screen)
        {
            return !_saved ? DefaultViewport(screen) : FitViewport(screen, _centerX * screen.X,
                _centerY * screen.Y, _width * (IsSquare ? screen.Y : screen.X), _height * screen.Y);
        }

        public RadarViewport DefaultViewport(Vector2 screen)
        {
            if (IsSquare)
                return Default(screen);
            RadarViewport radar = Default(screen);
            float gap = screen.X >= 1000 ? 48 : 24;
            float width = Kind == RadarPanelKind.Categories ? 200 : 380;
            float height = Kind == RadarPanelKind.Categories ? Math.Min(280, screen.Y * 0.4f) : Math.Min(480, screen.Y * 0.44f);
            height = (float)Math.Floor(height * 0.5f) * 2;
            float available = Kind == RadarPanelKind.Categories ? radar.X - gap - Margin :
                screen.X - Margin - (radar.X + radar.Width + gap);
            width = Math.Min(width, Math.Max(180, available));
            float x = Kind == RadarPanelKind.Categories ? radar.X - gap - width : radar.X + radar.Width + gap;
            return FitViewport(screen, x + width * 0.5f, screen.Y * 0.5f, width, height);
        }

        public RadarViewport FitViewport(Vector2 screen, float centerX, float centerY, float width, float height)
        {
            if (IsSquare)
                return Fit(screen, centerX, centerY, width);
            // Keep side-panel captions outside the scroll viewport, including
            // when the user moves or resizes the body toward a screen edge.
            float side = Math.Min(SideMargin, Math.Max(0, (screen.X - 1) / 3));
            float vertical = Math.Min(BottomMargin, Math.Max(0, (screen.Y - 1) / 3));
            float maxWidth = Math.Max(1, screen.X - side * 2), maxHeight = Math.Max(1, screen.Y - vertical * 2);
            width = Clamp(width, Math.Min(180, maxWidth), maxWidth);
            height = Clamp(height, Math.Min(64, maxHeight), maxHeight);
            centerX = Clamp(centerX, width * 0.5f + side, screen.X - width * 0.5f - side);
            centerY = Clamp(centerY, height * 0.5f + vertical, screen.Y - height * 0.5f - vertical);
            return new RadarViewport { X = centerX - width * 0.5f, Y = centerY - height * 0.5f, Width = width, Height = height };
        }

        public static RadarViewport Default(Vector2 screen) =>
            Fit(screen, screen.X * 0.5f, screen.Y * 0.5f, screen.Y * 0.5f);

        public static RadarViewport Fit(Vector2 screen, float centerX, float centerY, float size)
        {
            float maximum = Math.Max(1, Math.Min(screen.X - Margin * 2, screen.Y - Margin * 2));
            size = Clamp(size, Math.Min(160, maximum), maximum);
            float half = size * 0.5f;
            centerX = Clamp(centerX, half + Margin, screen.X - half - Margin);
            centerY = Clamp(centerY, half + Margin, screen.Y - half - Margin);
            return new RadarViewport { X = centerX - half, Y = centerY - half, Size = size };
        }

        public void Save(RadarViewport viewport, Vector2 screen)
        {
            if (_path == null || !Finite(screen.X) || !Finite(screen.Y) || screen.X <= 0 || screen.Y <= 0 ||
                !Finite(viewport.CenterX) || !Finite(viewport.CenterY) || !Finite(viewport.Width) ||
                !Finite(viewport.Height) || viewport.Width <= 0 || viewport.Height <= 0)
                throw new InvalidOperationException("No character or valid panel is available for saving.");
            float x = viewport.CenterX / screen.X, y = viewport.CenterY / screen.Y;
            float width = viewport.Width / (IsSquare ? screen.Y : screen.X), height = viewport.Height / screen.Y;
            string directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("RadarLayout");
                    writer.WriteAttributeString("version", IsSquare ? "1" : "2");
                    Write(writer, "centerX", x); Write(writer, "centerY", y);
                    if (IsSquare)
                        Write(writer, "size", height);
                    else
                    {
                        writer.WriteAttributeString("panel", Caption);
                        Write(writer, "width", width); Write(writer, "height", height);
                    }
                    writer.WriteEndElement();
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                _centerX = x; _centerY = y; _width = width; _height = height;
                _saved = true;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void Write(XmlWriter writer, string name, float value) =>
            writer.WriteAttributeString(name, value.ToString("R", CultureInfo.InvariantCulture));
        private static bool Read(XmlElement root, string name, out float value) =>
            float.TryParse(root.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Finite(value);
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
