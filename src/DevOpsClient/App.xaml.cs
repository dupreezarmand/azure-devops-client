using System.Windows;
using Telerik.Windows.Controls;
namespace DevOpsClient;
public partial class App : Application
{
    public App()
    {
        // The palette must be configured before App.xaml merges the implicit-style dictionaries.
        FluentPalette.LoadPreset(FluentPalette.ColorVariation.Dark);
        FluentPalette.Palette.ScrollBarsMode = Telerik.Windows.Controls.Theming.ScrollViewerScrollBarsMode.Compact;
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }
}
