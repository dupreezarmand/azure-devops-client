using System.Windows;
using System.Windows.Media;
using DevOpsClient.Core;
using Telerik.Windows.Controls;

namespace DevOpsClient;

internal static class UiAppearance
{
    public static string ThemeFamily { get; private set; } = "Fluent";
    public static string Theme { get; private set; } = "Dark";
    public static string FontFamilyName { get; private set; } = "Segoe UI";
    public static double FontScale { get; private set; } = 1.0;

    public static void Apply(ClientSettings settings)
    {
        ThemeFamily = settings.ThemeFamily;
        Theme = settings.Theme;
        FontFamilyName = settings.FontFamily;
        FontScale = settings.FontScale;
        ApplyTelerikPalette();
        TelerikThemeManager.Apply(ThemeFamily);
        if (Application.Current != null) ApplyResources(Application.Current.Resources);
    }

    private static void ApplyTelerikPalette()
    {
        var font = new FontFamily(FontFamilyName);
        switch (ThemeFamily)
        {
            case "Windows 11":
                // Telerik's Windows 11 palette consults the OS theme registry. Some headless
                // smoke environments do not expose that registry surface; the implicit theme
                // dictionaries still load correctly, so retain their defaults in that case.
                try
                {
                    Windows11Palette.LoadPreset(Theme == "Light" ? Windows11Palette.ColorVariation.Light : Windows11Palette.ColorVariation.Dark);
                    Windows11Palette.Palette.FontFamily = font;
                    Windows11Palette.Palette.ScrollBarsMode = Telerik.Windows.Controls.Theming.ScrollViewerScrollBarsMode.Compact;
                }
                catch (TypeInitializationException) { }
                break;
            case "Material":
                MaterialPalette.LoadPreset(Theme == "Light" ? MaterialPalette.ColorVariation.Light : MaterialPalette.ColorVariation.Dark);
                MaterialPalette.Palette.FontFamily = font;
                break;
            case "Office 2019":
                Office2019Palette.LoadPreset(Theme switch
                {
                    "Light" => Office2019Palette.ColorVariation.Light,
                    "High contrast" => Office2019Palette.ColorVariation.HighContrast,
                    _ => Office2019Palette.ColorVariation.Dark
                });
                Office2019Palette.Palette.FontFamily = font;
                break;
            default:
                FluentPalette.LoadPreset(Theme == "Light" ? FluentPalette.ColorVariation.Light : FluentPalette.ColorVariation.Dark);
                FluentPalette.Palette.FontFamily = font;
                FluentPalette.Palette.ScrollBarsMode = Telerik.Windows.Controls.Theming.ScrollViewerScrollBarsMode.Compact;
                break;
        }
    }

    public static double Size(double value) => Math.Round(value * FontScale, 1);

    public static Brush Brush(string source) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color(source))!);

    public static string Color(string source)
    {
        var value = source.ToUpperInvariant();
        if (Theme == "Dark") return source;
        if (Theme == "High contrast") return HighContrast(value);
        return Light(value);
    }

    private static string Light(string value) => value switch
    {
        "#10151D" => "#F4F7FA", "#141B25" => "#E8EEF4", "#151D27" => "#FFFFFF", "#161F2A" => "#F0F4F8",
        "#19222E" => "#FFFFFF", "#1A302B" => "#E2F2EB", "#1B2633" => "#F7F9FB", "#1C2633" => "#E5EBF1",
        "#1C2835" => "#E5EBF1", "#1F2B38" => "#E8F0F6", "#202B38" => "#FFFFFF", "#202C3A" => "#FFFFFF",
        "#243142" => "#DCE5ED", "#243D35" => "#D4EFE3", "#25384B" => "#D8E8F3", "#273442" => "#D6DFE8",
        "#293341" => "#C8D3DE", "#293543" => "#D6E0E8", "#2A3645" => "#C8D3DE", "#2D3B4C" => "#C2CFDB",
        "#303024" => "#FFF5D6", "#354253" => "#AEBCC8", "#355046" => "#C8E8D9", "#397B68" => "#4A9A7B",
        "#3B4D60" => "#9AAAB8", "#526275" => "#526275", "#5D7C96" => "#52677B", "#647589" => "#617488", "#67798D" => "#637689",
        "#71859C" => "#65788C", "#7D94AC" => "#64788C", "#8498B0" => "#63778A", "#899AAF" => "#66798C",
        "#93A1B3" => "#526273", "#93A8BF" => "#52677B", "#9DB5AC" => "#527363", "#9EB0C3" => "#5D7082",
        "#A3B0C0" => "#526273", "#A4B7CC" => "#52677A", "#A5B9CB" => "#52677A", "#A6B7CA" => "#526A7E",
        "#A8B9CD" => "#52677B", "#A8BDB7" => "#527363", "#A9C7E1" => "#426A85", "#B0C3D5" => "#536A7D",
        "#B1C0D1" => "#536A7D", "#BAC6D5" => "#52677A", "#BFAE87" => "#795A25", "#DABF91" => "#795A25",
        "#1D2A38" => "#E7EDF2", "#D0DCE8" => "#253746", "#D6E4F0" => "#263E50", "#E7CF98" => "#70581E", "#EAF0F6" => "#172431", "#F0808D" => "#C43E50",
        "#10251F" => "#0D3B2B", "#142A22" => "#0D3B2B", "#77CBE0" => "#267C9B", "#77E0BC" => "#16845E",
        "#85E6C4" => "#16845E", "#92C5E5" => "#267C9B", "#6EC6EF" => "#267C9B",
        _ => value
    };

    private static string HighContrast(string value) => value switch
    {
        "#10151D" or "#141B25" or "#151D27" or "#161F2A" or "#19222E" or "#1A302B" or "#1B2633" or "#1C2633" or "#1C2835" or "#1F2B38" or "#202B38" or "#202C3A" or "#243142" or "#243D35" or "#25384B" or "#273442" or "#293341" or "#293543" or "#2A3645" or "#2D3B4C" or "#303024" or "#354253" => "#000000",
        "#EAF0F6" or "#D6E4F0" or "#B0C3D5" or "#A4B7CC" or "#A5B9CB" or "#A8B9CD" or "#A9C7E1" or "#B1C0D1" or "#BAC6D5" => "#FFFFFF",
        "#5D7C96" or "#93A1B3" or "#93A8BF" or "#9EB0C3" or "#A3B0C0" or "#A6B7CA" or "#647589" or "#67798D" or "#71859C" or "#7D94AC" or "#8498B0" or "#899AAF" => "#E6E6E6",
        "#77E0BC" or "#85E6C4" or "#77CBE0" or "#92C5E5" or "#6EC6EF" => "#00FFB3",
        "#F0808D" or "#DABF91" or "#BFAE87" or "#E7CF98" => "#FFD43B",
        "#10251F" or "#142A22" => "#000000",
        _ => value
    };

    private static void ApplyResources(ResourceDictionary resources)
    {
        SetBrush(resources, "Ink", "#EAF0F6"); SetBrush(resources, "Muted", "#93A1B3"); SetBrush(resources, "Accent", "#77E0BC"); SetBrush(resources, "Line", "#293341");
        SetBrush(resources, "ShellBackground", "#10151D"); SetBrush(resources, "SidebarBackground", "#141B25"); SetBrush(resources, "Surface", "#151D27");
        SetBrush(resources, "SurfaceRaised", "#19222E"); SetBrush(resources, "InputBackground", "#111923"); SetBrush(resources, "ButtonBackground", "#202B38");
        SetBrush(resources, "Border", "#354253"); SetBrush(resources, "AccentText", "#10251F"); SetBrush(resources, "Selection", "#397B68");
        SetBrush(resources, "NoticeBackground", "#303024"); SetBrush(resources, "NoticeForeground", "#E7CF98");
        SetBrush(resources, "QueueTitle", "#EAF0F6"); SetBrush(resources, "QueueState", "#B0C3D5"); SetBrush(resources, "QueueMeta", "#9EB0C3");
        SetBrush(resources, "QueueHover", "#1F2B38"); SetBrush(resources, "QueueSelected", "#25384B"); SetBrush(resources, "QueueFocus", "#77CBE0");
    }

    private static void SetBrush(ResourceDictionary resources, string key, string source)
    {
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = (Color)ColorConverter.ConvertFromString(Color(source))!;
        else resources[key] = Brush(source);
    }
}
