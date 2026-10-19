using System;
using System.IO;
using System.Reflection;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.UI;
using ProfessionHandler.Generic;

namespace ProfessionHandler.Auto
{
    public sealed class Main : IAOPluginEntry
    {
        private IAOPluginEntry _handler;
        private string _directory;
        private bool _attempted;
        private bool _tornDown;

        public void Init(string pluginDir)
        {
            if (_attempted)
                throw new InvalidOperationException("Auto Handler was already initialized; eject before retrying.");
            _attempted = true;

            if (DynelManager.LocalPlayer == null || Game.IsZoning)
                throw new InvalidOperationException("Auto Handler needs a logged-in character that is not zoning.");
            HandlerStartup.EnsureAvailable();

            Profession profession = DynelManager.LocalPlayer.Profession;
            string entryType;
            string module = SelectModule(profession, out entryType);
            _directory = Path.GetFullPath(pluginDir);
            string path = Path.Combine(_directory, "ADDME-" + module + ".dll");
            if (!File.Exists(path))
                throw new FileNotFoundException("Auto Handler cannot find ADDME-" + module + ".dll. Copy the complete matching handler output beside ADDME-ProfessionHandler.Auto.dll.", path);

            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            Assembly assembly = Assembly.LoadFrom(path);
            Type type = assembly.GetType(entryType, true);
            if (!typeof(IAOPluginEntry).IsAssignableFrom(type))
                throw new InvalidOperationException(module + " does not expose the expected AOSharp plugin entry point.");

            _handler = (IAOPluginEntry)Activator.CreateInstance(type);
            Chat.WriteLine("Auto Handler: " + profession + " -> " + module);
            // Original Init registers the selected module's IPC types and retains its settings/UI paths.
            _handler.Init(Path.GetDirectoryName(path));
        }

        public void Teardown()
        {
            if (_tornDown)
                return;
            _tornDown = true;
            try
            {
                _handler?.Teardown();
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
            }
        }

        private Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            // Only resolve requests made by assemblies from this handler bundle.
            Assembly requester = args.RequestingAssembly;
            if (requester == null || requester.IsDynamic ||
                !string.Equals(Path.GetDirectoryName(requester.Location), _directory, StringComparison.OrdinalIgnoreCase))
                return null;

            string name = new AssemblyName(args.Name).Name;
            if (name == "AOSharp.Core" || name == "AOSharp.Common" || name == "AOSharp.Bootstrap")
                return null;
            string path = Path.Combine(_directory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static string SelectModule(Profession profession, out string entryType)
        {
            string suffix;
            string entryNamespace;
            switch (profession)
            {
                case Profession.Adventurer: suffix = "Adv"; entryNamespace = "Adventurer"; break;
                case Profession.Agent: suffix = "Agent"; entryNamespace = "Agent"; break;
                case Profession.Bureaucrat: suffix = "Crat"; entryNamespace = "Bureaucrat"; break;
                case Profession.Doctor: suffix = "Doc"; entryNamespace = "Doctor"; break;
                case Profession.Enforcer: suffix = "Enf"; entryNamespace = "Enforcer"; break;
                case Profession.Engineer: suffix = "Engi"; entryNamespace = "Engineer"; break;
                case Profession.Fixer: suffix = "Fixer"; entryNamespace = "Fixer"; break;
                case Profession.Keeper: suffix = "Keeper"; entryNamespace = "Keeper"; break;
                case Profession.MartialArtist: suffix = "MA"; entryNamespace = "MartialArtist"; break;
                case Profession.Metaphysicist: suffix = "MP"; entryNamespace = "Metaphysicist"; break;
                case Profession.NanoTechnician: suffix = "NT"; entryNamespace = "NanoTechnician"; break;
                case Profession.Shade: suffix = "Shade"; entryNamespace = "Shade"; break;
                case Profession.Soldier: suffix = "Soldier"; entryNamespace = "Soldier"; break;
                case Profession.Trader: suffix = "Trader"; entryNamespace = "Trader"; break;
                default: throw new NotSupportedException("Auto Handler has no handler for profession " + profession + ".");
            }
            entryType = "ProfessionHandler." + entryNamespace + ".Main";
            return "ProfessionHandler." + suffix;
        }
    }
}
