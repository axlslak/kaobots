using AOSharp.Core;

namespace ProfessionHandler.Enforcer
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new EnfProfessionHandler(PluginDirectory)));
        }
    }
}
