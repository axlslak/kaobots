using AOSharp.Core;

namespace ProfessionHandler.Soldier
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new SoldProfessionHandler(PluginDirectory)));
        }
    }
}
