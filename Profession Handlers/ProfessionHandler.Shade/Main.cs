using AOSharp.Core;

namespace ProfessionHandler.Shade
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new ShadeProfessionHandler(PluginDirectory)));
        }
    }
}
