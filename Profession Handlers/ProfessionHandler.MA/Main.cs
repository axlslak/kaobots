using AOSharp.Core;

namespace ProfessionHandler.MartialArtist
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new MAProfessionHandler(PluginDirectory)));
        }
    }
}
