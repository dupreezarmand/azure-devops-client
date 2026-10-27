using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
namespace DevOpsClient;

// A small native, selectable document renderer. It creates WPF runs only: no browser,
// HTML execution, external image downloads, or XAML deserialization of server content.
internal static class ReadableDocument
{
    private static Brush LinkBrush => UiAppearance.Brush("#92C5E5");
    private static Brush CodeBrush => UiAppearance.Brush("#1D2A38");

    public static bool LooksLikeHtml(string? content) =>
        !string.IsNullOrWhiteSpace(content) && Regex.IsMatch(content, @"<\s*(p|div|h[1-6]|ul|ol|li|pre|blockquote|br|a|strong|em|table|img)\b", RegexOptions.IgnoreCase);

    public static FlowDocumentScrollViewer CreateAuto(string content, Action<string>? openLink = null) =>
        Create(content, LooksLikeHtml(content), openLink);

    public static FlowDocumentScrollViewer Create(string content, bool html = true, Action<string>? openLink = null)
    {
        var document = new FlowDocument { FontFamily = new FontFamily(UiAppearance.FontFamilyName), FontSize = UiAppearance.Size(14), Foreground = UiAppearance.Brush("#D0DCE8"), Background = Brushes.Transparent, PagePadding = new Thickness(0), LineHeight = UiAppearance.Size(23) };
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) }; document.Blocks.Add(paragraph);
        void NewParagraph()
        {
            if (paragraph.Inlines.Count == 0) return;
            paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) }; document.Blocks.Add(paragraph);
        }
        if (string.IsNullOrWhiteSpace(content)) paragraph.Inlines.Add(new Run("No description provided."));
        else if (!html) RenderMarkdown(document, content, openLink);
        else
        {
            var safe = Regex.Replace(content, @"<(script|style|iframe)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase);
            var bold = 0; var italic = 0; var code = 0; var pre = 0; var listDepth = 0; Uri? linkTarget = null;
            var hasHtmlBlocks = Regex.IsMatch(safe, @"<(p|div|h[1-6]|ul|pre)\b", RegexOptions.IgnoreCase);
            foreach (var token in Regex.Split(safe, @"(<[^>]+>)"))
            {
                if (token.StartsWith('<') && token.EndsWith('>'))
                {
                    var match = Regex.Match(token, @"^<\s*(/?)\s*([a-zA-Z0-9]+)"); if (!match.Success) continue;
                    var closing = match.Groups[1].Length > 0; var tag = match.Groups[2].Value.ToLowerInvariant(); var delta = closing ? -1 : 1;
                    switch (tag)
                    {
                        case "a":
                            linkTarget = null;
                            if (!closing)
                            {
                                var href = Regex.Match(token, "href\\s*=\\s*[\"\u0027]([^\"\u0027]+)[\"\u0027]", RegexOptions.IgnoreCase);
                                if (href.Success && Uri.TryCreate(WebUtility.HtmlDecode(href.Groups[1].Value), UriKind.Absolute, out var uri) && uri.Scheme == "https") linkTarget = uri;
                            }
                            break;
                        case "strong": case "b": bold = Math.Max(0, bold + delta); break;
                        case "em": case "i": italic = Math.Max(0, italic + delta); break;
                        case "code": code = Math.Max(0, code + delta); break;
                        case "pre": NewParagraph(); pre = Math.Max(0, pre + delta); break;
                        case "h1": case "h2": case "h3": case "h4": NewParagraph(); bold = Math.Max(0, bold + delta); break;
                        case "br": paragraph.Inlines.Add(new LineBreak()); break;
                        case "ul": case "ol": NewParagraph(); listDepth = Math.Max(0, listDepth + delta); break;
                        case "li": NewParagraph(); if (!closing) paragraph.Inlines.Add(new Run(new string(' ', Math.Max(0, listDepth - 1) * 2) + "• ")); break;
                        case "p": case "div": case "tr": case "blockquote": NewParagraph(); break;
                        case "td": case "th": if (closing) paragraph.Inlines.Add(new Run("  |  ")); break;
                        case "img": paragraph.Inlines.Add(new Run("[Image — open in Azure DevOps to view]")); break;
                    }
                }
                else if (token.Length > 0)
                {
                    var text = WebUtility.HtmlDecode(token);
                    if (pre == 0 && hasHtmlBlocks) text = Regex.Replace(text, @"\s+", " ");
                    if (string.IsNullOrWhiteSpace(text) && paragraph.Inlines.Count == 0) continue;
                    var run = new Run(text) { FontWeight = bold > 0 ? FontWeights.SemiBold : FontWeights.Normal, FontStyle = italic > 0 ? FontStyles.Italic : FontStyles.Normal,
                        FontFamily = new FontFamily(code > 0 || pre > 0 ? "Cascadia Mono, Consolas" : UiAppearance.FontFamilyName), FontSize = UiAppearance.Size(code > 0 || pre > 0 ? 12 : 14),
                        Background = code > 0 || pre > 0 ? CodeBrush : Brushes.Transparent };
                    if (linkTarget != null && openLink != null)
                    {
                        var target = linkTarget.AbsoluteUri; var link = new Hyperlink(run) { Foreground = LinkBrush, NavigateUri = linkTarget };
                        link.RequestNavigate += (_, e) => { openLink(target); e.Handled = true; }; paragraph.Inlines.Add(link);
                    }
                    else paragraph.Inlines.Add(run);
                }
            }
        }
        return new FlowDocumentScrollViewer { Document = document, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsSelectionEnabled = true };
    }

    private static void RenderMarkdown(FlowDocument document, string content, Action<string>? openLink)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Paragraph? paragraph = null;
        var inFence = false;
        var codeLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraph == null || paragraph.Inlines.Count == 0) return;
            document.Blocks.Add(paragraph);
            paragraph = null;
        }
        void AddInlineLine(string line)
        {
            paragraph ??= new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            if (paragraph.Inlines.Count > 0) paragraph.Inlines.Add(new LineBreak());
            AddInline(paragraph, line, openLink);
        }
        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            var codeParagraph = new Paragraph { Margin = new Thickness(0, 2, 0, 14), Background = CodeBrush, Padding = new Thickness(10, 7, 10, 7) };
            codeParagraph.Inlines.Add(new Run(string.Join("\n", codeLines)) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = UiAppearance.Size(12) });
            document.Blocks.Add(codeParagraph);
            codeLines.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (Regex.IsMatch(line, @"^\s{0,3}```"))
            {
                if (inFence) { FlushCode(); inFence = false; }
                else { FlushParagraph(); inFence = true; }
                continue;
            }
            if (inFence) { codeLines.Add(raw); continue; }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }

            var heading = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
            if (heading.Success)
            {
                FlushParagraph();
                var level = heading.Groups[1].Length;
                var headingParagraph = new Paragraph { Margin = new Thickness(0, level <= 2 ? 7 : 3, 0, 10), FontSize = UiAppearance.Size(level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 }), FontWeight = FontWeights.SemiBold };
                AddInline(headingParagraph, heading.Groups[2].Value, openLink);
                document.Blocks.Add(headingParagraph);
                continue;
            }
            if (Regex.IsMatch(line, @"^\s{0,3}((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$"))
            {
                FlushParagraph();
                var rule = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                rule.Inlines.Add(new Run("────────────────────────────────"));
                document.Blocks.Add(rule);
                continue;
            }
            var quote = Regex.Match(line, @"^\s{0,3}>\s?(.*)$");
            if (quote.Success)
            {
                FlushParagraph();
                var quoteParagraph = new Paragraph { Margin = new Thickness(14, 0, 0, 10), BorderBrush = UiAppearance.Brush("#5D7C96"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0) };
                AddInline(quoteParagraph, quote.Groups[1].Value, openLink);
                document.Blocks.Add(quoteParagraph);
                continue;
            }
            var unordered = Regex.Match(line, @"^(\s*)[-+*]\s+(.+)$");
            var ordered = Regex.Match(line, @"^(\s*)(\d+)[.)]\s+(.+)$");
            if (unordered.Success || ordered.Success)
            {
                FlushParagraph();
                var indent = (unordered.Success ? unordered.Groups[1].Length : ordered.Groups[1].Length) / 2;
                var listParagraph = new Paragraph { Margin = new Thickness(16 + indent * 20, 0, 0, 5) };
                listParagraph.Inlines.Add(new Run(unordered.Success ? "• " : ordered.Groups[2].Value + ". "));
                AddInline(listParagraph, unordered.Success ? unordered.Groups[2].Value : ordered.Groups[3].Value, openLink);
                document.Blocks.Add(listParagraph);
                continue;
            }
            AddInlineLine(line);
        }
        if (inFence) FlushCode();
        FlushParagraph();
    }

    private static void AddInline(Paragraph paragraph, string text, Action<string>? openLink)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length && "\\`*_[]".Contains(text[index + 1]))
            {
                paragraph.Inlines.Add(new Run(text[index + 1].ToString())); index += 2; continue;
            }
            if (text[index] == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1) { paragraph.Inlines.Add(new Run(text[(index + 1)..end]) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = UiAppearance.Size(12), Background = CodeBrush }); index = end + 1; continue; }
            }
            var image = Regex.Match(text[index..], @"^!\[([^\]]*)\]\((https://[^)\s]+)\)");
            if (image.Success) { paragraph.Inlines.Add(new Run("[Image — open in Azure DevOps to view]")); index += image.Length; continue; }
            var link = Regex.Match(text[index..], @"^\[([^\]]+)\]\((https://[^)\s]+)\)");
            if (link.Success)
            {
                var label = link.Groups[1].Value; var target = link.Groups[2].Value;
                if (openLink == null) paragraph.Inlines.Add(new Run(label) { Foreground = LinkBrush });
                else
                {
                    var hyperlink = new Hyperlink(new Run(label)) { Foreground = LinkBrush, NavigateUri = new Uri(target) };
                    hyperlink.RequestNavigate += (_, e) => { openLink(target); e.Handled = true; };
                    paragraph.Inlines.Add(hyperlink);
                }
                index += link.Length; continue;
            }
            if ((text[index] == '*' || text[index] == '_') && index + 1 < text.Length && text[index + 1] == text[index])
            {
                var marker = text[index].ToString() + text[index]; var end = text.IndexOf(marker, index + 2, StringComparison.Ordinal);
                if (end > index + 2) { paragraph.Inlines.Add(new Run(text[(index + 2)..end]) { FontWeight = FontWeights.SemiBold }); index = end + 2; continue; }
            }
            if (text[index] == '*' || text[index] == '_')
            {
                var end = text.IndexOf(text[index], index + 1);
                if (end > index + 1 && !char.IsWhiteSpace(text[index + 1])) { paragraph.Inlines.Add(new Run(text[(index + 1)..end]) { FontStyle = FontStyles.Italic }); index = end + 1; continue; }
            }
            var next = index + 1;
            while (next < text.Length && !"`*_\\[".Contains(text[next])) next++;
            paragraph.Inlines.Add(new Run(text[index..next]));
            index = next;
        }
    }
}

