using AOSharp.Core;

namespace ProfessionHandler.Fixer
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new FixerProfessionHandler(PluginDirectory)));
        }
    }
}
