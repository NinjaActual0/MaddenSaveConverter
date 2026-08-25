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
        if (e.Args.Contains("--dump-fields"))
        {
            Shutdown(Cli.RunDumpFields(e.Args));
            return;
        }
        if (e.Args.Contains("--compare-inner"))
        {
            Shutdown(Cli.RunCompareInner(e.Args));
            return;
        }
        base.OnStartup(e);
    }
}
