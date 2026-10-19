using AOSharp.Core;

namespace ProfessionHandler.Doctor
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new DocProfessionHandler(PluginDirectory)));
        }
    }
}
