using AOSharp.Core;

namespace ProfessionHandler.Engineer
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new EngiProfessionHandler(PluginDirectory)));
        }
    }
}
