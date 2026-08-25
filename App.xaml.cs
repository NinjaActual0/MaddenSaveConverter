using System.Windows;

namespace MaddenSaveConverter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest"))
        {
            Shutdown(SelfTest.Run());
            return;
        }
        base.OnStartup(e);
    }
}
