using System;
using System.Globalization;
using System.IO;
using System.Xml;
using AOSharp.Common.GameData;

namespace Radar
{
    internal struct RadarViewport
    {
        public float X, Y, Size;
        public float CenterX => X + Size * 0.5f;
        public float CenterY => Y + Size * 0.5f;
    }

    // Store screen proportions rather than machine-specific absolute pixels.
    internal sealed class RadarLayout
    {
        private const float Margin = 8;
        private float _centerX = 0.5f, _centerY = 0.5f, _size = 0.5f;
        private string _path;
        private Identity _character = Identity.None;

        public string Load(Identity character)
        {
            if (_character == character)
                return null;
            _character = character;
            _centerX = _centerY = _size = 0.5f;
            _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AOSharp", "Radar", character.Instance.ToString("X8", CultureInfo.InvariantCulture), "layout.xml");
            try
            {
                if (!File.Exists(_path))
                    return null;
                var xml = new XmlDocument { XmlResolver = null };
                xml.Load(_path);
                XmlElement root = xml.DocumentElement;
                float x, y, size;
                if (root == null || root.Name != "RadarLayout" || root.GetAttribute("version") != "1" ||
                    !Read(root, "centerX", out x) || !Read(root, "centerY", out y) || !Read(root, "size", out size) ||
                    x < 0 || x > 1 || y < 0 || y > 1 || size <= 0 || size > 2)
                    throw new InvalidDataException("Invalid saved radar layout.");
                _centerX = x;
                _centerY = y;
                _size = size;
                return null;
            }
            catch (Exception ex)
            {
                return "Radar: saved layout could not be read; using the centred layout. " + ex.Message;
            }
        }

        public RadarViewport GetViewport(Vector2 screen)
        {
            return Fit(screen, _centerX * screen.X, _centerY * screen.Y, _size * screen.Y);
        }

        public static RadarViewport Default(Vector2 screen)
        {
            return Fit(screen, screen.X * 0.5f, screen.Y * 0.5f, screen.Y * 0.5f);
        }

        public static RadarViewport Fit(Vector2 screen, float centerX, float centerY, float size)
        {
            float maximum = Math.Max(1, Math.Min(screen.X - Margin * 2, screen.Y - Margin * 2));
            size = Math.Min(maximum, Math.Max(Math.Min(160, maximum), size));
            float half = size * 0.5f;
            centerX = Math.Max(half + Margin, Math.Min(screen.X - half - Margin, centerX));
            centerY = Math.Max(half + Margin, Math.Min(screen.Y - half - Margin, centerY));
            return new RadarViewport { X = centerX - half, Y = centerY - half, Size = size };
        }

        public void Save(RadarViewport viewport, Vector2 screen)
        {
            if (_path == null || screen.X <= 0 || screen.Y <= 0 || !Finite(viewport.Size) || viewport.Size <= 0)
                throw new InvalidOperationException("No character or screen is available for saving the radar layout.");

            float x = viewport.CenterX / screen.X, y = viewport.CenterY / screen.Y, size = viewport.Size / screen.Y;
            string directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var settings = new XmlWriterSettings { Indent = true };
                using (XmlWriter writer = XmlWriter.Create(temporary, settings))
                {
                    writer.WriteStartElement("RadarLayout");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("centerX", x.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("centerY", y.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("size", size.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                if (File.Exists(_path))
                    File.Replace(temporary, _path, null);
                else
                    File.Move(temporary, _path);
                // Commit only after the file has been written successfully.
                _centerX = x;
                _centerY = y;
                _size = size;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static bool Read(XmlElement root, string name, out float value)
        {
            return float.TryParse(root.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Finite(value);
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
