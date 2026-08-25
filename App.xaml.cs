using System.Windows;

namespace MaddenSaveConverter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest"))
        {
            Shutdown(SelfTest.Run(e.Args));
            return;
        }
        if (e.Args.Contains("--convert"))
        {
            Shutdown(Cli.RunConvert(e.Args));
            return;
        }
        base.OnStartup(e);
    }
}
