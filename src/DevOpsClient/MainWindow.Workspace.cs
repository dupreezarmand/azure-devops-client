using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevOpsClient.Core;
using Telerik.Windows.Controls;

namespace DevOpsClient;
public partial class MainWindow
{
    private WorkQueue assignedQueue = new([]);
    private readonly Dictionary<int, WorkItem> recentlyViewed = [];
    private readonly List<int> readingHistory = [];
    private readonly List<int> recentlyViewedOrder = [];
    private WorkItemDetails? readingDetails;
    private int? readingId;
    private CancellationTokenSource? readingCancellation;
    private int readingGeneration;
    private Task readingTask = Task.CompletedTask;
    private ListBox? queueList;
    private TextBlock? queueCount, queueEmpty;
    private ContentControl? readingHost, commentsHost;
    private IReadOnlyList<WorkItemComment> readingComments = [];
    private string? commentsNext, commentsError;
    private string descriptionDraft = "", acceptanceDraft = "", newCommentDraft = "", commentEditDraft = "";
    private int? editingCommentId;
    private bool editingContent, composingComment;
    private RadComboBox? readingStateEditor;
    private RadRichTextBox? descriptionRichEditor, acceptanceRichEditor, commentRichEditor;
    private bool commentsLoading, includeCompleted, selectingQueue;
    private int commentTotal, readingTab;
    private bool IsWorkPage => page is "Assigned to me" or "All work" or "Recently viewed";

    private void ResetReadingEdits()
    {
        editingContent = composingComment = false; descriptionDraft = acceptanceDraft = newCommentDraft = commentEditDraft = "";
        editingCommentId = null; readingStateEditor = null; descriptionRichEditor = acceptanceRichEditor = commentRichEditor = null;
    }

    private bool HasReadingDraft()
    {
        var item = readingDetails?.Item;
        if (item == null) return !string.IsNullOrWhiteSpace(newCommentDraft);
        if ((string?)readingStateEditor?.SelectedItem != null && (string?)readingStateEditor!.SelectedItem != item.State) return true;
        if (editingContent && (descriptionDraft != item.Description || acceptanceDraft != item.AcceptanceCriteria ||
            descriptionRichEditor?.Document.History.CanUndo == true || acceptanceRichEditor?.Document.History.CanUndo == true)) return true;
        if (!string.IsNullOrWhiteSpace(newCommentDraft)) return true;
        if (editingCommentId is int id)
        {
            var comment = readingComments.FirstOrDefault(value => value.Id == id);
            if (comment != null && (commentEditDraft != CommentSource(comment) || commentRichEditor?.Document.History.CanUndo == true)) return true;
        }
        return false;
    }

    private void TrackReadingDraft()
    {
        if (IsWorkPage && InspectorColumn.Width.Value == 0) hasDraft = HasReadingDraft;
    }

    private static string CommentSource(WorkItemComment comment) =>
        comment.EditableText.Length > 0 ? comment.EditableText : comment.Text;

    private static TextBox SourceEditor(string text, double minHeight, string tag)
    {
        var editor = new TextBox
        {
            Style = (Style)Application.Current.FindResource("MarkdownEditor"),
            Text = text,
            Tag = tag,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = minHeight,
            Padding = new Thickness(8, 6, 8, 6),
            FontFamily = new FontFamily(UiAppearance.FontFamilyName),
            FontSize = UiAppearance.Size(13),
            Background = UiAppearance.Brush("#111923"),
            Foreground = UiAppearance.Brush("#D0DCE8"),
            BorderBrush = UiAppearance.Brush("#354253"),
            SelectionBrush = UiAppearance.Brush("#397B68")
        };
        SpellCheck.SetIsEnabled(editor, true);
        return editor;
    }

    private Button PencilButton(string label, object tag, Action edit)
    {
        var button = ActionButton("✎", edit);
        button.Tag = tag;
        button.ToolTip = label + " (double-click the content or press F2)";
        button.Padding = new Thickness(7, 3, 7, 3);
        button.Margin = new Thickness(3);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.Opacity = 0;
        button.Focusable = true;
        Panel.SetZIndex(button, 2);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private Border InlineEditSurface(UIElement content, string label, object tag, Action edit)
    {
        var layer = new Grid { Background = Brushes.Transparent, Focusable = true };
        layer.Children.Add(content);
        var pencil = PencilButton(label, tag, edit);
        layer.Children.Add(pencil);
        var frame = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(3),
            Child = layer,
            ToolTip = "Double-click or press F2 to " + label.ToLowerInvariant() + "."
        };
        AutomationProperties.SetName(layer, label + " content");
        void SetActive(bool active)
        {
            pencil.Opacity = active ? 1 : 0;
            frame.Background = active ? Brush("#192430") : Brushes.Transparent;
            frame.BorderBrush = active ? Brush("#2A3645") : Brushes.Transparent;
        }
        frame.MouseEnter += (_, _) => SetActive(true);
        frame.MouseLeave += (_, _) => SetActive(layer.IsKeyboardFocusWithin);
        layer.IsKeyboardFocusWithinChanged += (_, _) => SetActive(layer.IsKeyboardFocusWithin || frame.IsMouseOver);
        layer.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 2 || busy) return;
            edit(); args.Handled = true;
        };
        layer.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is not (Key.F2 or Key.Enter) || busy) return;
            edit(); args.Handled = true;
        };
        return frame;
    }

    private WrapPanel RichTextToolbar(RadRichTextBox editor)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void Add(string text, string tip, Action action, FontWeight? weight = null, FontStyle? style = null)
        {
            var button = ActionButton(text, () => { action(); editor.Focus(); });
            button.Padding = new Thickness(7, 2, 7, 2); button.Margin = new Thickness(0, 0, 3, 0); button.ToolTip = tip;
            if (weight != null) button.FontWeight = weight.Value;
            if (style != null) button.FontStyle = style.Value;
            bar.Children.Add(button);
        }
        Add("B", "Bold (Ctrl+B)", editor.ToggleBold, FontWeights.Bold);
        Add("I", "Italic (Ctrl+I)", editor.ToggleItalic, style: FontStyles.Italic);
        Add("U", "Underline (Ctrl+U)", editor.ToggleUnderline);
        return bar;
    }

    private Border ReaderSection(string title, double fontSize = 11, bool uppercase = false)
    {
        return new Border
        {
            Background = Brush("#1A302B"),
            BorderBrush = Brush("#77E0BC"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Child = Text(uppercase ? title.ToUpperInvariant() : title, fontSize, fontSize >= 14 ? "#D6E4F0" : "#93A8BF", true)
        };
    }

    // FlowDocumentScrollViewer is intentionally kept non-scrolling so the reading pane has one
    // scrollbar. Telerik/WPF child controls can otherwise consume the wheel event before the
    // surrounding reader ScrollViewer receives it, especially when the pointer is over rich text.
    private static ScrollViewer ReadingScroll(UIElement content)
    {
        var viewer = new ScrollViewer
        {
            Content = content,
            CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta == 0 || viewer.ScrollableHeight <= 0) return;
            viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset - e.Delta / 3.0, 0, viewer.ScrollableHeight));
            e.Handled = true;
        };
        return viewer;
    }

    private void CancelReading()
    {
        readingGeneration++;
        readingCancellation?.Cancel(); readingCancellation?.Dispose(); readingCancellation = null;
    }
    private IEnumerable<WorkItem> QueueItems()
    {
        IEnumerable<WorkItem> source = page switch { "Assigned to me" => assignedQueue.Items, "Recently viewed" => recentlyViewedOrder.Select(id => recentlyViewed[id]), _ => snapshot.Items };
        var query = SearchBox.Text.Trim(); var type = TypeFilter.SelectedItem as string;
        return source.Where(i => (includeCompleted || i.Category is not ("Completed" or "Removed")) &&
            (string.IsNullOrEmpty(type) || type == "All types" || i.Type == type) &&
            (query.Length == 0 || $"{i.Id} {i.Title} {i.State} {i.AssignedTo} {i.Tags}".Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    private UIElement BuildInbox()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(350), MinWidth = 265 });
        grid.ColumnDefinitions.Add(new() { Width = new(7) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 380 });
        var left = new Grid(); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new());
        var tools = new StackPanel { Margin = new(7, 3, 7, 4) };
        var row = new DockPanel(); var open = ActionButton("Open #…", ShowOpenByNumber); open.Padding = new(7, 3, 7, 3); DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
        queueCount = Text("", 11, "#93A8BF"); queueCount.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(queueCount); tools.Children.Add(row);
        var completed = new CheckBox { Content = "Include completed", IsChecked = includeCompleted, Margin = new(0, 3, 0, 0), FontSize = 11 };
        completed.Click += (_, _) => { includeCompleted = completed.IsChecked == true; UpdateQueueList(); }; tools.Children.Add(completed); left.Children.Add(tools);
        queueList = new ListBox { Style = (Style)FindResource("QueueList"), ItemContainerStyle = (Style)FindResource("QueueRow"), ItemTemplate = (DataTemplate)FindResource("QueueItem"), SelectionMode = SelectionMode.Single };
        VirtualizingPanel.SetIsVirtualizing(queueList, true); VirtualizingPanel.SetVirtualizationMode(queueList, VirtualizationMode.Recycling);
        queueList.SelectionChanged += (_, _) =>
        {
            if (selectingQueue || queueList.SelectedItem is not WorkItem item || readingId == item.Id && readingDetails != null) return;
            if (busy || !DiscardDraft()) { SyncQueueSelection(); return; }
            hasDraft = null; readingTask = SelectReadingAsync(item.Id);
        };
        Grid.SetRow(queueList, 1); left.Children.Add(queueList);
        queueEmpty = Text("No items match this view. Try clearing the search or including completed items.", 13, "#93A8BF"); queueEmpty.Margin = new(18); Grid.SetRow(queueEmpty, 1); left.Children.Add(queueEmpty);
        var leftBorder = new Border { Background = Brush("#151D27"), BorderBrush = Brush("#2A3645"), BorderThickness = new(1), Child = left }; grid.Children.Add(leftBorder);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, Background = Brush("#263442"), ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext }; Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        readingHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var right = new Border { Background = Brush("#151D27"), BorderBrush = Brush("#2A3645"), BorderThickness = new(1), Padding = new(10, 6, 10, 6), Child = readingHost }; Grid.SetColumn(right, 2); grid.Children.Add(right);
        UpdateQueueList(); BuildReadingPane();
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsWorkPage || busy || service == null || readingDetails != null) return;
            var first = readingId ?? QueueItems().FirstOrDefault()?.Id;
            if (first.HasValue) readingTask = SelectReadingAsync(first.Value, false);
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        return grid;
    }
    private void UpdateQueueList()
    {
        if (!IsWorkPage || queueList == null) return;
        var items = QueueItems().ToArray(); selectingQueue = true; queueList.ItemsSource = items; queueList.SelectedItem = items.FirstOrDefault(i => i.Id == readingId); selectingQueue = false;
        if (queueCount != null) queueCount.Text = $"{items.Length} items" + (page == "Assigned to me" && assignedQueue.LimitReached ? " · limit reached" : "");
        if (queueEmpty != null) queueEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SyncQueueSelection()
    {
        if (queueList == null) return;
        selectingQueue = true; queueList.SelectedItem = queueList.Items.Cast<WorkItem>().FirstOrDefault(i => i.Id == readingId); selectingQueue = false;
    }
    private async Task SelectReadingAsync(int id, bool addHistory = true)
    {
        if (service == null || !IsWorkPage || busy) return;
        CancelReading(); var generation = readingGeneration;
        readingCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = readingCancellation.Token;
        if (addHistory && readingId.HasValue && readingId != id) readingHistory.Add(readingId.Value);
        CloseInspector(); hasDraft = null; ResetReadingEdits(); readingId = id; readingDetails = null; readingTab = 0;
        readingComments = []; commentsNext = commentsError = null; commentTotal = 0; commentsLoading = true;
        SyncQueueSelection(); BuildReadingPane();
        try
        {
            var details = await service.GetDetailsAsync(id, token);
            if (generation != readingGeneration || token.IsCancellationRequested) return;
            readingDetails = details; recentlyViewed[id] = details.Item; recentlyViewedOrder.Remove(id); recentlyViewedOrder.Insert(0, id); BuildReadingPane();
            if (page == "Recently viewed") UpdateQueueList();
            await LoadReadingCommentsAsync(id, null, generation, token);
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested && generation == readingGeneration) ShowReadingError("Loading this item timed out. Try again."); }
        catch (Exception ex) { if (generation == readingGeneration && !token.IsCancellationRequested) ShowReadingError(ReadError(ex)); }
    }
    private static string ReadError(Exception ex) => ex switch {
        HttpRequestException { StatusCode: not null } http => http.Message,
        HttpRequestException => "Could not reach Azure DevOps. Check your network and retry.",
        ArgumentException arg => arg.Message,
        _ => "This work item could not be loaded. Check access and try again." };
    private void ShowReadingError(string message)
    {
        if (readingHost == null || !IsWorkPage) return;
        var panel = Stack(Text("Could not open #" + readingId, 18, bold: true), Text(message, 13, "#DABF91"));
        var retry = ActionButton("Retry", () => { if (readingId.HasValue) readingTask = SelectReadingAsync(readingId.Value, false); }); retry.Margin = new(0, 16, 0, 0); panel.Children.Add(retry); readingHost.Content = panel;
    }
    private void BuildReadingPane()
    {
        if (readingHost == null || !IsWorkPage) return;
        if (readingDetails == null)
        {
            readingHost.Content = Text(readingId.HasValue ? $"Loading #{readingId}…" : "Select a work item to read its description, comments, and linked work.", 14, "#93A8BF"); return;
        }
        var details = readingDetails; var item = details.Item;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); var actions = new WrapPanel { Margin = new(0, 0, 0, 4) };
        var back = ActionButton("← Back", () =>
        {
            if (busy || readingHistory.Count == 0 || !DiscardDraft()) return;
            var id = readingHistory[^1]; readingHistory.RemoveAt(readingHistory.Count - 1); hasDraft = null; readingTask = SelectReadingAsync(id, false);
        }); back.Padding = new(7, 3, 7, 3); back.IsEnabled = readingHistory.Count > 0; actions.Children.Add(back);
        var copy = ActionButton("Copy #" + item.Id, () => { try { Clipboard.SetText(item.Id.ToString()); } catch { ShowNotice("The clipboard is currently unavailable."); } }); copy.Padding = new(7, 3, 7, 3); copy.Margin = new(4, 0, 0, 0); actions.Children.Add(copy);
        if (!settings.UseDemoData) { var web = ActionButton("Open in Azure DevOps ↗", () => OpenUrl(new Uri(settings.ProjectUri(), "_workitems/edit/" + item.Id).AbsoluteUri)); web.Padding = new(7, 3, 7, 3); web.Margin = new(4, 0, 0, 0); actions.Children.Add(web); }
        header.Children.Add(actions);
        var parent = details.Relations.FirstOrDefault(r => r.Relationship == "Parent");
        if (parent != null) { var parentButton = RelationButton(parent, true); parentButton.Margin = new(0, 0, 0, 5); header.Children.Add(parentButton); }
        header.Children.Add(Text($"{item.Type.ToUpperInvariant()}  #{item.Id}", 11, item.KindColor, true));
        var title = Text(item.Title, 21, bold: true); title.Margin = new(0, 4, 0, 6); header.Children.Add(title);
        header.Children.Add(Text(item.AssignedTo + "   ·   " + item.ShortIteration + "   ·   Priority " + item.Priority, 12, "#A4B7CC"));
        var states = snapshot.Types.FirstOrDefault(t => t.Name == item.Type)?.States.Select(s => s.Name).ToList() ?? [];
        if (!states.Contains(item.State)) states.Add(item.State);
        var stateRow = new WrapPanel { Margin = new(0, 5, 0, 5) };
        var stateLabel = Text("State", 12, "#93A8BF"); stateLabel.VerticalAlignment = VerticalAlignment.Center; stateLabel.Margin = new(0, 0, 6, 0); stateRow.Children.Add(stateLabel);
        var state = new RadComboBox { ItemsSource = states, SelectedItem = item.State, MinWidth = 150 }; readingStateEditor = state; stateRow.Children.Add(state);
        Button save = null!; save = ActionButton("Save status", () => { readingTask = SaveReadingStateAsync(); }); save.Margin = new(4, 0, 0, 0); save.Padding = new(8, 4, 8, 4); save.IsEnabled = false; stateRow.Children.Add(save);
        state.SelectionChanged += (_, _) => { save.IsEnabled = (string?)state.SelectedItem != item.State; TrackReadingDraft(); };
        TrackReadingDraft(); header.Children.Add(stateRow); root.Children.Add(header);
        var tabs = new TabControl { SelectedIndex = readingTab };
        var descriptionStack = new StackPanel();
        if (editingContent)
        {
            var visualDescription = ReadableDocument.LooksLikeHtml(item.Description);
            descriptionStack.Children.Add(ReaderSection("Editing description", 12));
            var guidance = Text(visualDescription
                ? "Edit the rendered content directly. Select text for more formatting options."
                : "Markdown is supported. The saved content remains Markdown.", 11, "#93A8BF");
            guidance.Margin = new Thickness(0, 5, 0, 5); descriptionStack.Children.Add(guidance);
            Button saveContent = null!;
            void RefreshSaveState()
            {
                saveContent.IsEnabled = visualDescription || acceptanceRichEditor != null || descriptionDraft != item.Description ||
                    (item.AcceptanceCriteria.Length > 0 && acceptanceDraft != item.AcceptanceCriteria);
                TrackReadingDraft();
            }
            FrameworkElement descriptionEditor;
            if (visualDescription)
            {
                var rich = RichTextEditing.CreateHtmlEditor(descriptionDraft, 310, "DescriptionEditor");
                descriptionRichEditor = rich;
                descriptionStack.Children.Add(RichTextToolbar(rich));
                rich.DocumentContentChanged += (_, _) => { descriptionDraft = RichTextEditing.ExportHtml(rich); RefreshSaveState(); };
                descriptionEditor = rich;
            }
            else
            {
                var markdown = SourceEditor(descriptionDraft, 250, "DescriptionEditor");
                markdown.TextChanged += (_, _) => { descriptionDraft = markdown.Text; RefreshSaveState(); };
                descriptionEditor = markdown;
            }
            descriptionStack.Children.Add(descriptionEditor);
            FrameworkElement? acceptanceEditor = null;
            if (item.AcceptanceCriteria.Length > 0)
            {
                var acceptanceHeading = ReaderSection("Acceptance criteria · separate Azure DevOps field", 12);
                acceptanceHeading.Margin = new Thickness(0, 8, 0, 5); descriptionStack.Children.Add(acceptanceHeading);
                if (ReadableDocument.LooksLikeHtml(item.AcceptanceCriteria))
                {
                    var rich = RichTextEditing.CreateHtmlEditor(acceptanceDraft, 170, "AcceptanceEditor");
                    acceptanceRichEditor = rich;
                    descriptionStack.Children.Add(RichTextToolbar(rich));
                    rich.DocumentContentChanged += (_, _) => { acceptanceDraft = RichTextEditing.ExportHtml(rich); RefreshSaveState(); };
                    acceptanceEditor = rich;
                }
                else
                {
                    var markdown = SourceEditor(acceptanceDraft, 130, "AcceptanceEditor");
                    markdown.TextChanged += (_, _) => { acceptanceDraft = markdown.Text; RefreshSaveState(); };
                    acceptanceEditor = markdown;
                }
                descriptionStack.Children.Add(acceptanceEditor);
            }
            var editorActions = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            saveContent = ActionButton(settings.UseDemoData ? "Save demo description" : "Save description", () => { readingTask = SaveContentAsync(); }, true);
            saveContent.Tag = "SaveDescription"; saveContent.Padding = new Thickness(9, 5, 9, 5); editorActions.Children.Add(saveContent);
            var cancelContent = ActionButton("Cancel", () =>
            {
                editingContent = false; descriptionDraft = acceptanceDraft = ""; TrackReadingDraft(); BuildReadingPane();
            });
            cancelContent.Padding = new Thickness(9, 5, 9, 5); cancelContent.Margin = new Thickness(5, 0, 0, 0); editorActions.Children.Add(cancelContent);
            descriptionStack.Children.Add(editorActions);
            RefreshSaveState();
            _ = Dispatcher.BeginInvoke(new Action(() => descriptionEditor.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            void BeginDescriptionEdit()
            {
                editingContent = true; descriptionDraft = item.Description; acceptanceDraft = item.AcceptanceCriteria; readingTab = 0; BuildReadingPane();
            }
            var description = ReadableDocument.CreateAuto(item.Description, OpenUrl); description.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            descriptionStack.Children.Add(InlineEditSurface(description, "Edit description", "EditDescription", BeginDescriptionEdit));
            if (item.AcceptanceCriteria.Length > 0)
            {
                // This is a separate Azure DevOps field, not part of the authored Description HTML.
                // Give it a quiet section marker without changing any WYSIWYG content.
                var acceptanceHeading = ReaderSection("Acceptance criteria", 15);
                acceptanceHeading.Margin = new Thickness(0, 10, 0, 6);
                descriptionStack.Children.Add(acceptanceHeading);
                var acceptance = ReadableDocument.CreateAuto(item.AcceptanceCriteria, OpenUrl); acceptance.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; descriptionStack.Children.Add(acceptance);
            }
        }
        tabs.Items.Add(new TabItem { Header = "Description", Content = ReadingScroll(descriptionStack) });
        commentsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        tabs.Items.Add(new TabItem { Header = "Comments", Content = commentsHost });
        var linked = new StackPanel();
        if (details.RelationsWarning != null) { var warning = Text(details.RelationsWarning, 12, "#DABF91"); warning.Margin = new(0, 0, 0, 12); linked.Children.Add(warning); }
        foreach (var group in details.Relations.GroupBy(r => r.Relationship).OrderBy(g => g.Key == "Parent" ? 0 : g.Key == "Child" ? 1 : 2))
        {
            var relationHeading = ReaderSection(group.Key + (group.Key == "Child" ? "ren" : "") + " · " + group.Count(), 10, true);
            relationHeading.Margin = new Thickness(0, 10, 0, 5);
            linked.Children.Add(relationHeading);
            foreach (var related in group) { var link = RelationButton(related); link.Margin = new(0, 0, 0, 8); linked.Children.Add(link); }
        }
        if (details.Relations.Count == 0) linked.Children.Add(Text("No parent, children, or related work item links.", 13, "#93A8BF"));
        tabs.Items.Add(new TabItem { Header = "Related (" + details.Relations.Count + ")", Content = ReadingScroll(linked) });
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs) readingTab = tabs.SelectedIndex; };
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var footer = Text($"Changed {item.ChangedAt.ToLocalTime():dd MMM yyyy HH:mm}   ·   Revision {item.Revision}" + (item.Tags.Length > 0 ? "   ·   " + item.Tags : ""), 10, "#7D94AC"); footer.Margin = new(0, 12, 0, 0); Grid.SetRow(footer, 2); root.Children.Add(footer);
        readingHost.Content = root; BuildCommentsPane();
        async Task SaveContentAsync()
        {
            if (busy || service == null || state.SelectedItem is not string target) return;
            if (descriptionRichEditor?.Document.History.CanUndo == true) descriptionDraft = RichTextEditing.ExportHtml(descriptionRichEditor);
            if (acceptanceRichEditor?.Document.History.CanUndo == true) acceptanceDraft = RichTextEditing.ExportHtml(acceptanceRichEditor);
            await RunAsync(async () =>
            {
                var updated = await service.UpdateAsync(item,
                    new(item.Title, target, item.Tags, item.Priority, Description: descriptionDraft,
                        AcceptanceCriteria: item.AcceptanceCriteria.Length > 0 ? acceptanceDraft : null), lifetime.Token);
                UpdateKnownItem(updated); readingDetails = details with { Item = updated };
                editingContent = false; descriptionDraft = acceptanceDraft = ""; hasDraft = null;
                BuildReadingPane(); UpdateQueueList();
                ShowNotice(settings.UseDemoData ? $"#{item.Id} description saved in this demo session." : $"#{item.Id} description saved to Azure DevOps.");
            });
            TrackReadingDraft();
        }
        async Task SaveReadingStateAsync()
        {
            if (busy || service == null || state.SelectedItem is not string target || target == item.State) return;
            save.IsEnabled = false;
            await RunAsync(async () =>
            {
                var updated = await service.UpdateAsync(item, new(item.Title, target, item.Tags, item.Priority), lifetime.Token);
                UpdateKnownItem(updated); readingDetails = details with { Item = updated }; hasDraft = null; BuildReadingPane(); UpdateQueueList();
                ShowNotice(settings.UseDemoData ? $"#{item.Id} status saved in this demo session." : $"#{item.Id} status saved to Azure DevOps.");
            });
            if (readingDetails?.Item.Revision == item.Revision) save.IsEnabled = true;
        }
    }
    private void UpdateKnownItem(WorkItem item)
    {
        snapshot = snapshot with { Items = snapshot.Items.Select(i => i.Id == item.Id ? item : i).ToArray() };
        assignedQueue = assignedQueue with { Items = assignedQueue.Items.Select(i => i.Id == item.Id ? item : i).ToArray() };
        if (recentlyViewed.ContainsKey(item.Id)) recentlyViewed[item.Id] = item;
    }
    private Button RelationButton(RelatedWorkItem related, bool compact = false)
    {
        var item = related.Item;
        var content = compact ? Stack(Text("↑ Parent  #" + related.Id + "  " + (item?.Title ?? "Unavailable"), 11, "#92C5E5")) :
            Stack(Text("#" + related.Id + "  " + (item?.Title ?? "Unavailable work item"), 14, "#BADAF1", true), Text(item == null ? "Deleted, inaccessible, or temporarily unavailable" : item.Type + " · " + item.State + " · " + item.AssignedTo, 11, "#93A8BF"));
        var button = new Button { Content = content, Padding = compact ? new(0, 3, 0, 3) : new(12), Background = compact ? Brushes.Transparent : Brush("#1D2A38"), BorderThickness = new(compact ? 0 : 1), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = item != null, ToolTip = "Read linked work item #" + related.Id };
        button.Click += (_, _) => { if (!busy && DiscardDraft()) { hasDraft = null; readingTask = SelectReadingAsync(related.Id); } }; return button;
    }
    private async Task LoadReadingCommentsAsync(int id, string? next, int generation, CancellationToken token)
    {
        if (service == null) return;
        commentsLoading = true; commentsError = null; BuildCommentsPane();
        try
        {
            var result = await service.GetCommentsAsync(id, next, token);
            if (generation != readingGeneration || token.IsCancellationRequested) return;
            readingComments = (next == null ? result.Comments : readingComments.Concat(result.Comments)).DistinctBy(c => c.Id).ToArray();
            commentsNext = result.ContinuationToken; commentTotal = result.TotalCount;
        }
        catch (OperationCanceledException) { if (token.IsCancellationRequested) return; commentsError = "Comments timed out. Retry to load this discussion."; }
        catch (Exception ex) { if (generation != readingGeneration || token.IsCancellationRequested) return; commentsError = ReadError(ex); }
        finally { if (generation == readingGeneration && !token.IsCancellationRequested) { commentsLoading = false; BuildCommentsPane(); } }
    }
    private void BuildCommentsPane()
    {
        if (commentsHost == null || !IsWorkPage) return;
        var panel = new StackPanel();
        panel.Children.Add(ReaderSection(commentsLoading ? "Loading discussion…" : $"{readingComments.Count} of {commentTotal} comments"));
        if (!commentsLoading)
        {
            if (!composingComment)
            {
                var add = ActionButton("＋  Write a comment…", () => { composingComment = true; BuildCommentsPane(); });
                add.Tag = "AddComment"; add.ToolTip = "Start a Markdown comment";
                add.Padding = new Thickness(10, 6, 10, 6); add.Margin = new Thickness(0, 6, 0, 0);
                add.HorizontalContentAlignment = HorizontalAlignment.Left; panel.Children.Add(add);
            }
            else
            {
                var compose = ReaderSection("New comment · Markdown", 12); compose.Margin = new Thickness(0, 6, 0, 5); panel.Children.Add(compose);
                var composer = SourceEditor(newCommentDraft, 90, "CommentComposer");
                var composeActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                Button post = null!; post = ActionButton(settings.UseDemoData ? "Add demo comment" : "Post comment", () => { readingTask = PostCommentAsync(); }, true);
                post.Tag = "PostComment"; post.Padding = new Thickness(9, 5, 9, 5); post.IsEnabled = !string.IsNullOrWhiteSpace(newCommentDraft); composeActions.Children.Add(post);
                var cancel = ActionButton("Cancel", () => { composingComment = false; newCommentDraft = ""; TrackReadingDraft(); BuildCommentsPane(); });
                cancel.Padding = new Thickness(9, 5, 9, 5); cancel.Margin = new Thickness(5, 0, 0, 0); composeActions.Children.Add(cancel);
                composer.TextChanged += (_, _) => { newCommentDraft = composer.Text; post.IsEnabled = !string.IsNullOrWhiteSpace(newCommentDraft); TrackReadingDraft(); };
                panel.Children.Add(composer); panel.Children.Add(composeActions);
            }
        }
        foreach (var comment in readingComments)
        {
            var authorText = comment.Author + "   ·   " + comment.CreatedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm") + (comment.Version > 1 ? "   ·   edited" : "");
            var author = Text(authorText, 12, "#A9C7E1", true); author.VerticalAlignment = VerticalAlignment.Center;
            var metadataContent = new Grid(); metadataContent.ColumnDefinitions.Add(new()); metadataContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); metadataContent.Children.Add(author);
            void BeginCommentEdit()
            {
                if (editingCommentId != null) return;
                commentRichEditor = null; editingCommentId = comment.Id; commentEditDraft = CommentSource(comment); BuildCommentsPane();
            }
            Button? edit = null;
            if (editingCommentId != comment.Id)
            {
                edit = PencilButton("Edit comment", "EditComment:" + comment.Id, BeginCommentEdit);
                edit.ToolTip = "Edit comment (F2). Azure DevOps permits editing comments you own.";
                edit.Margin = new Thickness(0); edit.IsEnabled = editingCommentId == null;
                Grid.SetColumn(edit, 1); metadataContent.Children.Add(edit);
            }
            var metadata = new Border
            {
                Background = Brush("#1D2A38"),
                BorderBrush = Brush("#2D3B4C"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 10, 6),
                Child = metadataContent
            };
            UIElement bodyContent;
            if (editingCommentId == comment.Id)
            {
                var visualComment = comment.IsHtml && !comment.IsMarkdown;
                var editorPanel = new StackPanel();
                editorPanel.Children.Add(Text(visualComment ? "Edit the rendered comment" : "Edit comment · Markdown supported", 11, "#93A8BF", true));
                var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var saveEdit = ActionButton(settings.UseDemoData ? "Save demo comment" : "Save comment", () => { readingTask = SaveCommentAsync(comment); }, true);
                saveEdit.Tag = "SaveComment:" + comment.Id; saveEdit.Padding = new Thickness(9, 5, 9, 5); actions.Children.Add(saveEdit);
                void RefreshCommentSave()
                {
                    saveEdit.IsEnabled = visualComment || !string.IsNullOrWhiteSpace(commentEditDraft) && commentEditDraft != CommentSource(comment);
                    TrackReadingDraft();
                }
                FrameworkElement editor;
                if (visualComment)
                {
                    var rich = RichTextEditing.CreateHtmlEditor(commentEditDraft, 150, "CommentEditor:" + comment.Id);
                    commentRichEditor = rich;
                    rich.Margin = new Thickness(0, 5, 0, 0); editorPanel.Children.Add(RichTextToolbar(rich));
                    rich.DocumentContentChanged += (_, _) => { commentEditDraft = RichTextEditing.ExportHtml(rich); RefreshCommentSave(); };
                    editor = rich;
                }
                else
                {
                    var markdown = SourceEditor(commentEditDraft, 100, "CommentEditor:" + comment.Id); markdown.Margin = new Thickness(0, 5, 0, 0);
                    markdown.TextChanged += (_, _) => { commentEditDraft = markdown.Text; RefreshCommentSave(); };
                    editor = markdown;
                }
                editorPanel.Children.Add(editor);
                var cancelEdit = ActionButton("Cancel", () => { editingCommentId = null; commentEditDraft = ""; commentRichEditor = null; TrackReadingDraft(); BuildCommentsPane(); });
                cancelEdit.Padding = new Thickness(9, 5, 9, 5); cancelEdit.Margin = new Thickness(5, 0, 0, 0); actions.Children.Add(cancelEdit);
                editorPanel.Children.Add(actions); bodyContent = editorPanel; RefreshCommentSave();
                _ = Dispatcher.BeginInvoke(new Action(() => editor.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                var body = ReadableDocument.Create(comment.Text, comment.IsHtml, OpenUrl); body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; bodyContent = body;
            }
            var bodySection = new Border { Padding = new Thickness(10, 8, 10, 6), Child = bodyContent };
            var card = new Border
            {
                Background = Brush("#192430"),
                BorderBrush = Brush("#2A3645"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = Stack(metadata, bodySection),
                Margin = new Thickness(0, 8, 0, 0)
            };
            if (edit != null)
            {
                void SetEditVisible(bool visible) => edit.Opacity = visible ? 1 : 0;
                card.MouseEnter += (_, _) => SetEditVisible(true);
                card.MouseLeave += (_, _) => SetEditVisible(card.IsKeyboardFocusWithin);
                card.IsKeyboardFocusWithinChanged += (_, _) => SetEditVisible(card.IsKeyboardFocusWithin || card.IsMouseOver);
                card.Focusable = true;
                card.PreviewMouseLeftButtonDown += (_, args) =>
                {
                    if (args.ClickCount != 2 || busy || editingCommentId != null) return;
                    BeginCommentEdit(); args.Handled = true;
                };
                card.PreviewKeyDown += (_, args) =>
                {
                    if (args.Key is not (Key.F2 or Key.Enter) || busy || editingCommentId != null) return;
                    BeginCommentEdit(); args.Handled = true;
                };
            }
            panel.Children.Add(card);
        }
        if (!commentsLoading && readingComments.Count == 0 && commentsError == null) { var empty = Text("No comments on this item.", 13, "#93A8BF"); empty.Margin = new(0, 16, 0, 0); panel.Children.Add(empty); }
        if (commentsError != null) { var error = Text(commentsError, 12, "#DABF91"); error.Margin = new(0, 14, 0, 0); panel.Children.Add(error); }
        if (!commentsLoading && (commentsNext != null || commentsError != null))
        {
            var load = ActionButton(commentsError != null ? "Retry comments" : "Load more comments", () =>
            {
                if (readingId.HasValue && readingCancellation != null && !commentsLoading) readingTask = LoadReadingCommentsAsync(readingId.Value, commentsNext, readingGeneration, readingCancellation.Token);
            }); load.Margin = new(0, 16, 0, 0); panel.Children.Add(load);
        }
        commentsHost.Content = ReadingScroll(panel);
        TrackReadingDraft();

        async Task PostCommentAsync()
        {
            if (busy || service == null || readingId is not int id || string.IsNullOrWhiteSpace(newCommentDraft)) return;
            await RunAsync(async () =>
            {
                var added = await service.AddCommentAsync(id, newCommentDraft, lifetime.Token);
                readingComments = readingComments.Concat([added]).DistinctBy(comment => comment.Id).ToArray();
                commentTotal = Math.Max(commentTotal + 1, readingComments.Count); newCommentDraft = ""; composingComment = false;
                BuildCommentsPane(); ShowNotice(settings.UseDemoData ? "Comment added to this demo session." : $"Comment added to work item #{id}.");
            });
            TrackReadingDraft();
        }

        async Task SaveCommentAsync(WorkItemComment original)
        {
            if (commentRichEditor?.Document.History.CanUndo == true) commentEditDraft = RichTextEditing.ExportHtml(commentRichEditor);
            if (busy || service == null || readingId is not int id || string.IsNullOrWhiteSpace(commentEditDraft)) return;
            await RunAsync(async () =>
            {
                var updated = await service.UpdateCommentAsync(id, original, commentEditDraft, lifetime.Token);
                if (updated.Author == "Unknown author") updated = updated with { Author = original.Author };
                if (updated.CreatedAt == DateTimeOffset.MinValue) updated = updated with { CreatedAt = original.CreatedAt };
                readingComments = readingComments.Select(comment => comment.Id == original.Id ? updated : comment).ToArray();
                editingCommentId = null; commentEditDraft = ""; commentRichEditor = null; BuildCommentsPane();
                ShowNotice(settings.UseDemoData ? "Comment updated in this demo session." : $"Comment #{original.Id} updated.");
            });
            TrackReadingDraft();
        }
    }
    private void ShowOpenByNumber()
    {
        if (busy || service == null) return;
        var box = new TextBox { MinWidth = 260, Margin = new(0, 12, 0, 12) };
        var message = Text("Enter a work item number (with or without #).", 12, "#93A8BF");
        var dialog = new Window { Owner = this, Title = "Open work item", Width = 390, Height = 220, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#151D27"), Foreground = Brush("#EAF0F6"), ShowInTaskbar = false };
        var open = ActionButton("Open", () =>
        {
            if (!int.TryParse(box.Text.Trim().TrimStart('#'), out var id) || id <= 0) { message.Text = "Enter a positive work item number."; return; }
            if (!DiscardDraft()) return;
            dialog.DialogResult = true; hasDraft = null;
            if (!IsWorkPage) { page = "Assigned to me"; CloseInspector(); Render(); }
            readingTask = SelectReadingAsync(id);
        }, true);
        open.IsDefault = true; dialog.Content = new Border { Padding = new(22), Child = Stack(message, box, open) }; dialog.Loaded += (_, _) => box.Focus(); dialog.ShowDialog();
    }
}




