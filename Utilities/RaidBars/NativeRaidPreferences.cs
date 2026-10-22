using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using AOSharp.Common.Unmanaged.DataTypes;
using AOSharp.Common.Unmanaged.Interfaces;

namespace RaidBars
{
    // Use the client's supported DValue archive loader. No guessed window names,
    // memory offsets, patched DLL instructions, fake roster or replacement bars.
    internal sealed class NativeRaidPreferences
    {
        private const int CharacterCategory = 3;
        private readonly string _directory, _characterPath;
        private bool _backedUp;

        public NativeRaidPreferences(string directory)
        {
            Directory.CreateDirectory(directory);
            _directory = NativeDirectory(directory);
            _characterPath = CharacterPath();
        }

        public string UndoPath => Path.Combine(_directory, "undo.xml");
        public string SnapshotPath => Path.Combine(_directory, "native-current.xml");

        public XmlDocument Capture()
        {
            CheckOwner();
            // Delete the old snapshot before asking the client, so a failed
            // native export cannot be mistaken for a successful fresh read.
            if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath);
            using (StdString path = StdString.Create(SnapshotPath))
                if (!SaveConfig(path.Pointer, CharacterCategory))
                    throw new IOException("The client could not export its current character preferences.");
            XmlDocument snapshot = Read(SnapshotPath);
            XmlElement bars = Find(snapshot, "RaidBarConfig"), locked = Find(snapshot, "LockRaidBars");
            if (bars == null || (bars.Name != "Archive" && bars.Name != "Value") || locked?.Name != "Value")
                throw new InvalidOperationException("This client did not export RaidBarConfig and LockRaidBars; nothing was changed.");
            return snapshot;
        }

        public XmlDocument Apply(Func<XmlDocument, XmlDocument> makePatch, Func<XmlDocument, bool> verify)
        {
            CheckOwner();
            XmlDocument current = Capture();
            XmlDocument patch = makePatch(current);
            ValidatePatch(patch);
            XmlDocument before = Extract(current);
            if (!_backedUp)
            {
                // Keep only these two settings. Never copy other characters'
                // preferences or restore a complete Prefs.xml over a live client.
                SaveBackup(before);
                _backedUp = true;
            }
            try
            {
                Import(patch);
                XmlDocument after = Capture();
                if (!verify(after))
                    throw new InvalidOperationException("The client's preference read-back did not match the requested layout.");
                return after;
            }
            catch (Exception applyError)
            {
                try
                {
                    XmlDocument restore = RestorePatch(before, patch);
                    Import(restore);
                    XmlDocument restored = Capture();
                    if (!SameSettings(restore, restored))
                        throw new InvalidOperationException("Rollback read-back differs from the previous settings.");
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException("Apply failed, and restoring the previous raid settings also failed. " +
                        "The pre-edit backup is " + UndoPath + ". Apply: " + applyError.Message + "; restore: " + rollbackError.Message);
                }
                throw new InvalidOperationException("Raid settings were restored after a failed apply. " + applyError.Message, applyError);
            }
        }

        private void SaveBackup(XmlDocument document)
        {
            string temporary = UndoPath + ".tmp";
            try
            {
                document.Save(temporary);
                if (File.Exists(UndoPath)) File.Replace(temporary, UndoPath, null);
                else File.Move(temporary, UndoPath);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { /* A failed cleanup must not hide the backup result. */ }
            }
        }

        public XmlDocument Undo()
        {
            if (!File.Exists(UndoPath)) throw new FileNotFoundException("There is no pre-edit raid layout to restore yet.", UndoPath);
            XmlDocument original = Read(UndoPath);
            // Undo must not overwrite its own target on a freshly loaded plugin.
            _backedUp = true;
            XmlDocument restore = null;
            return Apply(current => restore = RestorePatch(original, current), after => SameSettings(restore, after));
        }

        private void Import(XmlDocument patch)
        {
            CheckOwner();
            ValidatePatch(patch);
            string file = Path.Combine(_directory, "native-patch.xml");
            patch.Save(file);
            using (StdString path = StdString.Create(file))
                if (!LoadConfig(path.Pointer, CharacterCategory, false))
                    throw new IOException("The client rejected the raid preference patch.");
        }

        private static XmlDocument RestorePatch(XmlDocument original, XmlDocument changed)
        {
            XmlDocument restore = Extract(original);
            XmlElement archive = Find(restore, "RaidBarConfig"), added = Find(changed, "RaidBarConfig");
            if (archive?.Name != "Archive" || added?.Name != "Archive") return restore;
            // LoadConfig merges archive fields. Restore original slots exactly,
            // and explicitly disable slots that this edit introduced. Retain
            // their dormant frame records instead of changing the live archive
            // to a speculative intermediate Variant type to force replacement.
            for (int team = 0; team < RaidBarLayout.Teams; team++)
                for (int slot = 0; slot < RaidBarLayout.Slots; slot++)
                {
                    string key = "t" + team + "p" + slot;
                    foreach (string name in new[] { "Config" + key, key + "Enabled" })
                    {
                        if (archive.ChildNodes.OfType<XmlElement>().Any(e => e.GetAttribute("name") == name)) continue;
                        XmlElement value = added.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.GetAttribute("name") == name);
                        if (value == null) continue;
                        var copy = (XmlElement)restore.ImportNode(value, true);
                        if (name.EndsWith("Enabled", StringComparison.Ordinal)) copy.SetAttribute("value", "false");
                        archive.AppendChild(copy);
                    }
                }
            return restore;
        }

        private void CheckOwner()
        {
            if (!string.Equals(_characterPath, CharacterPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The active character preference path changed; reload Raid Bars on this character.");
        }

        private static string CharacterPath()
        {
            string path = Preferences.GetCharacterPath();
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("The character preference path is unavailable.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NativeDirectory(string directory)
        {
            directory = Path.GetFullPath(directory);
            if (directory.All(c => c < 128)) return directory;
            // SDK StdString.Create encodes ASCII. Use an existing Windows short
            // alias when available rather than silently replacing a user's
            // non-ASCII path characters with '?' in a native file operation.
            var shortPath = new StringBuilder(32768);
            uint count = GetShortPathName(directory, shortPath, (uint)shortPath.Capacity);
            string result = shortPath.ToString();
            if (count == 0 || count >= (uint)shortPath.Capacity || result.Any(c => c >= 128))
                throw new IOException("The current AOSharp SDK needs an ASCII or Windows short path for the Raid Bars data folder.");
            return result;
        }

        public static XmlDocument Read(string path)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            if (document.DocumentElement == null) throw new InvalidDataException("Empty native preferences document.");
            return document;
        }

        public static XmlDocument NewDocument()
        {
            var document = new XmlDocument { XmlResolver = null };
            document.AppendChild(document.CreateElement("Root"));
            return document;
        }

        public static XmlElement Find(XmlDocument document, string name) =>
            document.DocumentElement?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.GetAttribute("name") == name);

        public static bool Locked(XmlDocument document) =>
            string.Equals(Find(document, "LockRaidBars")?.GetAttribute("value"), "true", StringComparison.OrdinalIgnoreCase);

        public static void SetLock(XmlDocument document, bool locked)
        {
            XmlElement value = Find(document, "LockRaidBars");
            if (value == null)
            {
                value = document.CreateElement("Value");
                value.SetAttribute("name", "LockRaidBars");
                document.DocumentElement.AppendChild(value);
            }
            value.SetAttribute("value", locked ? "true" : "false");
        }

        private static XmlDocument Extract(XmlDocument document)
        {
            XmlDocument result = NewDocument();
            foreach (string name in new[] { "RaidBarConfig", "LockRaidBars" })
            {
                XmlElement value = Find(document, name);
                if (value == null) throw new InvalidDataException("Missing native preference " + name);
                result.DocumentElement.AppendChild(result.ImportNode(value, true));
            }
            return result;
        }

        private static void ValidatePatch(XmlDocument document)
        {
            if (document.DocumentElement == null) throw new InvalidDataException("Empty raid patch.");
            foreach (XmlElement value in document.DocumentElement.ChildNodes.OfType<XmlElement>())
                if (!((value.GetAttribute("name") == "RaidBarConfig" && (value.Name == "Archive" || value.Name == "Value")) ||
                    (value.GetAttribute("name") == "LockRaidBars" && value.Name == "Value")))
                    throw new InvalidDataException("Raid Bars may only import RaidBarConfig and LockRaidBars.");
        }

        private static bool SameSettings(XmlDocument wanted, XmlDocument actual)
        {
            foreach (string name in new[] { "RaidBarConfig", "LockRaidBars" })
                if (Canonical(Find(wanted, name)) != Canonical(Find(actual, name))) return false;
            return true;
        }

        private static string Canonical(XmlElement element)
        {
            if (element == null) return "<missing>";
            // Native XML serialization may reorder fields and attributes.
            return element.Name + "[" + string.Join(";", element.Attributes.Cast<XmlAttribute>()
                .OrderBy(a => a.Name).Select(a => a.Name + "=" + a.Value)) + "]{" +
                string.Join(";", element.ChildNodes.OfType<XmlElement>().Select(Canonical).OrderBy(s => s, StringComparer.Ordinal)) + "}";
        }

        // The same exported functions already used by AOSharp's DistributedValue
        // wrapper, with the native bool return retained to detect failures.
        [DllImport("Utils.dll", EntryPoint = "?SaveConfig@DistributedValue_c@@SA_NABV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@W4DValueCategory_e@@@Z", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SaveConfig(IntPtr path, int category);

        [DllImport("Utils.dll", EntryPoint = "?LoadConfig@DistributedValue_c@@SA_NABV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@W4DValueCategory_e@@_N@Z", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool LoadConfig(IntPtr path, int category, [MarshalAs(UnmanagedType.U1)] bool addVariables);

        [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
    }
}
