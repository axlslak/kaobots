using AOSharp.Core;

namespace ProfessionHandler.Metaphysicist
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new MPProfessionHandler(PluginDirectory)));
        }
    }
}
