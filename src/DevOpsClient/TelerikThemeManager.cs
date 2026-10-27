using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevOpsClient;

// Telerik's documented runtime-switching path is NoXaml plus implicit resource dictionaries.
// Keep theme dictionaries in one place so adding another Telerik family does not leak theme
// assembly URIs into the view or appearance code.
internal static class TelerikThemeManager
{
    private static readonly string[] DictionaryNames =
    [
        "System.Windows.xaml",
        "Telerik.Windows.Controls.xaml",
        "Telerik.Windows.Controls.Input.xaml",
        "Telerik.Windows.Controls.GridView.xaml",
        "Telerik.Windows.Controls.RichTextBox.xaml"
    ];

    public static string CurrentFamily { get; private set; } = "Fluent";

    public static void Apply(string family)
    {
        if (Application.Current == null || string.Equals(CurrentFamily, family, StringComparison.Ordinal)) return;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        foreach (var dictionary in dictionaries.Where(IsTelerikThemeDictionary).ToArray()) dictionaries.Remove(dictionary);

        var sources = Sources(family).ToArray();
        for (var index = sources.Length - 1; index >= 0; index--)
            dictionaries.Insert(0, new ResourceDictionary { Source = sources[index] });

        RefreshShellStyles(Application.Current.Resources);
        CurrentFamily = family;
    }

    private static void RefreshShellStyles(ResourceDictionary resources)
    {
        if (resources[typeof(Button)] is not Style buttonBase) return;

        var primary = new Style(typeof(Button), buttonBase);
        primary.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Accent")));
        primary.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentText")));
        primary.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        primary.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        var nav = new Style(typeof(Button), buttonBase);
        nav.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        nav.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        nav.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Muted")));
        nav.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        nav.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        nav.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
        nav.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        resources["Primary"] = primary;
        resources["Nav"] = nav;
    }

    private static bool IsTelerikThemeDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.Contains("/Telerik.Windows.Themes.", StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<Uri> Sources(string family)
    {
        var assembly = family switch
        {
            "Fluent" => "Telerik.Windows.Themes.Fluent",
            "Windows 11" => "Telerik.Windows.Themes.Windows11",
            "Material" => "Telerik.Windows.Themes.Material",
            "Office 2019" => "Telerik.Windows.Themes.Office2019",
            _ => throw new ArgumentException("Choose a supported Telerik theme family.", nameof(family))
        };

        return DictionaryNames.Select(name => new Uri($"/{assembly};component/Themes/{name}", UriKind.RelativeOrAbsolute));
    }
}
