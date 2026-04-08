using System.Windows;
using CostSim.Presentation;

namespace CostSim;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.ApplySavedTheme();
    }
}
