using AOSharp.Core;

namespace ProfessionHandler.NanoTechnician
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new NTProfessionHandler(PluginDirectory)));
        }
    }
}
