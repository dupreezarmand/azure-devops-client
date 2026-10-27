using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Telerik.Windows.Controls;
using Telerik.Windows.Documents.FormatProviders.Html;
using Telerik.Windows.Documents.Model;

namespace DevOpsClient;

// Azure DevOps stores its rich-text work-item fields and legacy comments as HTML.
// Keep that transport detail here: the workspace only deals in a visual RadDocument.
internal static partial class RichTextEditing
{
    public static RadRichTextBox CreateHtmlEditor(string html, double height, object tag)
    {
        var provider = CreateProvider();
        var editor = new RadRichTextBox
        {
            Tag = tag,
            Document = provider.Import(string.IsNullOrWhiteSpace(html) ? "<p></p>" : html),
            Height = height,
            MinHeight = Math.Min(height, 120),
            LayoutMode = DocumentLayoutMode.Flow,
            DocumentInheritsDefaultStyleSettings = true,
            FontFamily = new FontFamily(UiAppearance.FontFamilyName),
            FontSize = UiAppearance.Size(14),
            Foreground = UiAppearance.Brush("#D0DCE8"),
            Background = UiAppearance.Brush("#111923"),
            BorderBrush = UiAppearance.Brush("#354253"),
            BorderThickness = new Thickness(1),
            IsReadOnly = false,
            IsSelectionMiniToolBarEnabled = true,
            IsContextMenuEnabled = true,
            AcceptsReturn = true,
            AcceptsTab = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        editor.Document.DocumentContentChangedInterval = TimeSpan.Zero;
        return editor;
    }

    public static string ExportHtml(RadRichTextBox editor)
    {
        var exported = CreateProvider().Export(editor.Document);
        var body = BodyRegex().Match(exported);
        return body.Success ? body.Groups[1].Value.Trim() : exported.Trim();
    }

    private static HtmlFormatProvider CreateProvider()
    {
        var import = new HtmlImportSettings { UseDefaultStylesheetForFontProperties = true };
        // Do not let server-authored descriptions perform arbitrary network fetches while
        // being imported. The original source remains untouched until an actual edit occurs.
        import.LoadImageFromUrl += (_, args) =>
        {
            // Keep the URI so the HTML exporter can round-trip the image reference, while
            // marking the request handled prevents the importer from downloading it.
            if (Uri.TryCreate(args.Url, UriKind.Absolute, out var source)) args.ImageElement.UriSource = source;
            args.Handled = true;
        };
        return new HtmlFormatProvider
        {
            ImportSettings = import,
            ExportSettings = new HtmlExportSettings
            {
                DocumentExportLevel = DocumentExportLevel.Fragment,
                StylesExportMode = StylesExportMode.Inline,
                StyleRepositoryExportMode = StyleRepositoryExportMode.DontExportStyles,
                ExportFontStylesAsTags = true,
                ExportBoldAsStrong = true,
                ExportItalicAsEm = true,
                ExportHeadingsAsTags = true,
                ExportEmptyDocumentAsEmptyString = true,
                ImageExportMode = ImageExportMode.UriSource
            }
        };
    }

    [GeneratedRegex(@"<body\b[^>]*>([\s\S]*?)</body\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();
}
