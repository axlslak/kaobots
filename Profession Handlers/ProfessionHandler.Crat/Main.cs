using AOSharp.Core;

namespace ProfessionHandler.Bureaucrat
{
    public class Main : AOPluginEntry
    {
        public override void Run()
        {
            Generic.HandlerStartup.Start(() =>
                Generic.Combat.ProfessionHandler.Set(new CratProfessionHandler(PluginDirectory)));
        }
    }
}
