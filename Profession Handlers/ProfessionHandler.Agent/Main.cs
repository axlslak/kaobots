using AOSharp.Core;

namespace ProfessionHandler.Agent
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new AgentProfessionHandler(PluginDirectory)));
        }
    }
}
