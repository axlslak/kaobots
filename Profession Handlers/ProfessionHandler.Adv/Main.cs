using AOSharp.Core;

namespace ProfessionHandler.Adventurer
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new AdvProfessionHandler(PluginDirectory)));
        }
    }
}
