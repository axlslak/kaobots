using AOSharp.Core;

namespace ProfessionHandler.Trader
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new TraderProfessionHandler(PluginDirectory)));
        }
    }
}
