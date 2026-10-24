using System.Net.Http;
using System.Windows;
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
    private bool commentsLoading, includeCompleted, selectingQueue;
    private int commentTotal, readingTab;
    private bool IsWorkPage => page is "Assigned to me" or "All work" or "Recently viewed";

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
        var tools = new StackPanel { Margin = new(12, 10, 12, 12) };
        var row = new DockPanel(); var open = ActionButton("Open #…", ShowOpenByNumber); open.Padding = new(10, 6, 10, 6); DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
        queueCount = Text("", 11, "#93A8BF"); queueCount.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(queueCount); tools.Children.Add(row);
        var completed = new CheckBox { Content = "Include completed", IsChecked = includeCompleted, Margin = new(0, 12, 0, 0), FontSize = 11 };
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
        var right = new Border { Background = Brush("#151D27"), BorderBrush = Brush("#2A3645"), BorderThickness = new(1), Padding = new(22, 18, 22, 14), Child = readingHost }; Grid.SetColumn(right, 2); grid.Children.Add(right);
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
        CloseInspector(); hasDraft = null; readingId = id; readingDetails = null; readingTab = 0;
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
        var header = new StackPanel(); var actions = new WrapPanel { Margin = new(0, 0, 0, 13) };
        var back = ActionButton("← Back", () =>
        {
            if (busy || readingHistory.Count == 0 || !DiscardDraft()) return;
            var id = readingHistory[^1]; readingHistory.RemoveAt(readingHistory.Count - 1); hasDraft = null; readingTask = SelectReadingAsync(id, false);
        }); back.Padding = new(9, 5, 9, 5); back.IsEnabled = readingHistory.Count > 0; actions.Children.Add(back);
        var copy = ActionButton("Copy #" + item.Id, () => { try { Clipboard.SetText(item.Id.ToString()); } catch { ShowNotice("The clipboard is currently unavailable."); } }); copy.Padding = new(9, 5, 9, 5); copy.Margin = new(8, 0, 0, 0); actions.Children.Add(copy);
        if (!settings.UseDemoData) { var web = ActionButton("Open in Azure DevOps ↗", () => OpenUrl(new Uri(settings.ProjectUri(), "_workitems/edit/" + item.Id).AbsoluteUri)); web.Padding = new(9, 5, 9, 5); web.Margin = new(8, 0, 0, 0); actions.Children.Add(web); }
        header.Children.Add(actions);
        var parent = details.Relations.FirstOrDefault(r => r.Relationship == "Parent");
        if (parent != null) { var parentButton = RelationButton(parent, true); parentButton.Margin = new(0, 0, 0, 10); header.Children.Add(parentButton); }
        header.Children.Add(Text($"{item.Type.ToUpperInvariant()}  #{item.Id}", 11, item.KindColor, true));
        var title = Text(item.Title, 21, bold: true); title.Margin = new(0, 7, 0, 10); header.Children.Add(title);
        header.Children.Add(Text(item.AssignedTo + "   ·   " + item.ShortIteration + "   ·   Priority " + item.Priority, 12, "#A4B7CC"));
        var states = snapshot.Types.FirstOrDefault(t => t.Name == item.Type)?.States.Select(s => s.Name).ToList() ?? [];
        if (!states.Contains(item.State)) states.Add(item.State);
        var stateRow = new WrapPanel { Margin = new(0, 12, 0, 12) };
        var stateLabel = Text("State", 12, "#93A8BF"); stateLabel.VerticalAlignment = VerticalAlignment.Center; stateLabel.Margin = new(0, 0, 10, 0); stateRow.Children.Add(stateLabel);
        var state = new RadComboBox { ItemsSource = states, SelectedItem = item.State, MinWidth = 150 }; stateRow.Children.Add(state);
        Button save = null!; save = ActionButton("Save status", () => { _ = SaveReadingStateAsync(); }); save.Margin = new(8, 0, 0, 0); save.Padding = new(12, 9, 12, 9); save.IsEnabled = false; stateRow.Children.Add(save);
        state.SelectionChanged += (_, _) => save.IsEnabled = (string?)state.SelectedItem != item.State;
        if (InspectorColumn.Width.Value == 0) hasDraft = () => (string?)state.SelectedItem != item.State; header.Children.Add(stateRow); root.Children.Add(header);
        var tabs = new TabControl { SelectedIndex = readingTab };
        var descriptionStack = new StackPanel();
        var description = ReadableDocument.CreateAuto(item.Description, OpenUrl); description.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; descriptionStack.Children.Add(description);
        if (item.AcceptanceCriteria.Length > 0)
        {
            var acceptanceHeading = Text("Acceptance criteria", 16, "#D6E4F0", true); acceptanceHeading.Margin = new(0, 10, 0, 6); descriptionStack.Children.Add(acceptanceHeading);
            var acceptance = ReadableDocument.CreateAuto(item.AcceptanceCriteria, OpenUrl); acceptance.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; descriptionStack.Children.Add(acceptance);
        }
        tabs.Items.Add(new TabItem { Header = "Description", Content = new ScrollViewer { Content = descriptionStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
        commentsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        tabs.Items.Add(new TabItem { Header = "Comments", Content = commentsHost });
        var linked = new StackPanel();
        if (details.RelationsWarning != null) { var warning = Text(details.RelationsWarning, 12, "#DABF91"); warning.Margin = new(0, 0, 0, 12); linked.Children.Add(warning); }
        foreach (var group in details.Relations.GroupBy(r => r.Relationship).OrderBy(g => g.Key == "Parent" ? 0 : g.Key == "Child" ? 1 : 2))
        {
            linked.Children.Add(Label(group.Key + (group.Key == "Child" ? "ren" : "") + " · " + group.Count()));
            foreach (var related in group) { var link = RelationButton(related); link.Margin = new(0, 0, 0, 8); linked.Children.Add(link); }
        }
        if (details.Relations.Count == 0) linked.Children.Add(Text("No parent, children, or related work item links.", 13, "#93A8BF"));
        tabs.Items.Add(new TabItem { Header = "Related (" + details.Relations.Count + ")", Content = new ScrollViewer { Content = linked } });
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs) readingTab = tabs.SelectedIndex; };
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var footer = Text($"Changed {item.ChangedAt.ToLocalTime():dd MMM yyyy HH:mm}   ·   Revision {item.Revision}" + (item.Tags.Length > 0 ? "   ·   " + item.Tags : ""), 10, "#7D94AC"); footer.Margin = new(0, 12, 0, 0); Grid.SetRow(footer, 2); root.Children.Add(footer);
        readingHost.Content = root; BuildCommentsPane();
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
        panel.Children.Add(Text(commentsLoading ? "Loading discussion…" : $"{readingComments.Count} of {commentTotal} comments", 11, "#93A8BF"));
        foreach (var comment in readingComments)
        {
            var author = Text(comment.Author + "   ·   " + comment.CreatedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm"), 12, "#A9C7E1", true); author.Margin = new(0, 0, 0, 9);
            var body = ReadableDocument.Create(comment.Text, comment.IsHtml, OpenUrl); body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var card = Card(Stack(author, body), "#192430", new(14)); card.Margin = new(0, 14, 0, 0); panel.Children.Add(card);
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
        commentsHost.Content = new ScrollViewer { Content = panel };
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




