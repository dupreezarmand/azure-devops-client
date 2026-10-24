using System.Windows;
using Telerik.Windows.Controls;
namespace DevOpsClient;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StyleManager.ApplicationTheme = new FluentTheme();
        FluentPalette.LoadPreset(FluentPalette.ColorVariation.Dark);
        base.OnStartup(e);
    }
}
