using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevOpsClient.Core;
using Telerik.Windows.Controls;

namespace DevOpsClient;
public partial class MainWindow : Window
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private ClientSettings settings = new();
    private IProjectService? service;
    private ProjectSnapshot snapshot = new([], [], []);
    private string page = "Assigned to me";
    private bool busy, filtering, ready;
    private readonly CancellationTokenSource lifetime = new();
    private WorkItem? selected;
    private Func<bool>? hasDraft;
    private static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.RegisterAttached("BaseFontSize", typeof(double), typeof(MainWindow), new FrameworkPropertyMetadata(0d));
    private static Brush Brush(string value) => UiAppearance.Brush(value);
    private static TextBlock Text(string value, double size = 13, string color = "#EAF0F6", bool bold = false) => new() { Text = value, FontSize = size, FontFamily = new FontFamily(UiAppearance.FontFamilyName), Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static Border Card(UIElement child, string background = "#19222E", Thickness? padding = null) => new() { Child = child, Background = Brush(background), BorderBrush = Brush("#2A3645"), BorderThickness = new(1), CornerRadius = new(10), Padding = padding ?? new(20) };
    private static StackPanel Stack(params UIElement[] children) { var p = new StackPanel(); foreach (var child in children) p.Children.Add(child); return p; }
    private static TextBlock Label(string value) { var t = Text(value.ToUpperInvariant(), 10, "#93A1B3", true); t.Margin = new(0, 18, 0, 8); return t; }
    private Button ActionButton(string title, Action action, bool primary = false)
    {
        var b = new Button { Content = primary ? Text(title, 13, "#10251F", true) : title, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource(primary ? "Primary" : typeof(Button)) };
        b.Click += (_, _) => action(); return b;
    }
    public MainWindow()
    {
        InitializeComponent();
        WindowPlacementStore.Restore(this, AppContext.BaseDirectory);
        UiAppearance.Apply(settings);
        ApplyFontScale();
        SourceInitialized += (_, _) => { var enabled = 1; _ = DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int)); };
        Loaded += async (_, _) =>
        {
            ready = true;
            try { settings = Environment.GetCommandLineArgs().Contains("--smoke") ? new() : SettingsStore.Load(AppContext.BaseDirectory); settings.Validate(); UiAppearance.Apply(settings); ApplyFontScale(); }
            catch { ShowNotice("Connection settings could not be read or validated. Review Settings to continue."); page = "Settings"; Render(); return; }
            await ConnectAsync();
            if (Environment.GetCommandLineArgs().Contains("--smoke")) await SmokeAsync();
        };
        Closing += (_, e) => { if (!DiscardDraft()) { e.Cancel = true; return; } WindowPlacementStore.Save(this, AppContext.BaseDirectory); };
        Closed += (_, _) => { CancelReading(); lifetime.Cancel(); service?.Dispose(); lifetime.Dispose(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K) { if (!IsWorkPage && page is not ("Board" or "Work items")) Go("Assigned to me"); SearchBox.Focus(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.G) { ShowOpenByNumber(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { ShowNew(); e.Handled = true; }
            if (e.Key == Key.Escape && DiscardDraft()) CloseInspector();
        };
    }
    private void ShowNotice(string? message) { Notice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; NoticeText.Text = message; }
    private void ReportError(string message) { ShowNotice(message); StatusText.Text = "Operation failed · see the message above. Check connection settings or refresh to retry."; }
    private async Task RunAsync(Func<Task> work)
    {
        if (busy) return;
        busy = true; RefreshButton.IsEnabled = NewButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try { await work(); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) ReportError("The request timed out. Check your connection and refresh."); }
        catch (HttpRequestException ex) { ReportError(ex.StatusCode.HasValue ? ex.Message : "Could not reach Azure DevOps. Check your network connection and project URL."); }
        catch (ArgumentException ex) { ReportError(ex.Message); }
        catch (Exception) { ReportError("The operation could not be completed. Check your settings and try again."); }
        finally { busy = false; Mouse.OverrideCursor = null; RefreshButton.IsEnabled = true; NewButton.IsEnabled = service != null && snapshot.Types.Count > 0; }
    }
    private async Task ConnectAsync()
    {
        await RunAsync(async () =>
        {
            StatusText.Text = settings.UseDemoData ? "Preparing demo workspace…" : "Connecting to Azure DevOps…";
            IProjectService candidate = settings.UseDemoData ? new DemoProjectService() : new AzureDevOpsService(settings);
            ProjectSnapshot next; WorkQueue nextAssigned;
            try { next = await candidate.LoadAsync(lifetime.Token); nextAssigned = await candidate.LoadAssignedAsync(lifetime.Token); }
            catch { candidate.Dispose(); throw; }
            CancelReading(); readingHistory.Clear(); recentlyViewed.Clear(); recentlyViewedOrder.Clear(); readingDetails = null; readingId = null; service?.Dispose(); service = candidate; snapshot = next; assignedQueue = nextAssigned;
            ProjectLabel.Text = settings.ProjectName;
            ModeLabel.Text = settings.UseDemoData ? "●  DEMO WORKSPACE" : "●  LIVE CONNECTION";
            CloseInspector(); PopulateFilters(); Render(); SetLoadedStatus();
            ShowNotice(snapshot.DashboardWarning ?? (IsWorkPage && assignedQueue.LimitReached ? "Your assigned-work limit was reached. Increase the limit in Settings to see more." : null) ?? (snapshot.LimitReached ? $"Showing the {settings.MaxWorkItems} most recently changed items. Overview metrics describe this loaded set." : null));
        });
    }
    private void SetLoadedStatus() => StatusText.Text = $"{(settings.UseDemoData ? "Demo · changes last for this session" : "Azure DevOps · " + settings.ProjectName)}   /   {snapshot.Items.Count} items loaded   /   Updated {DateTime.Now:HH:mm}";
    private async Task RefreshAsync()
    {
        if (busy || !DiscardDraft()) return;
        if (service == null) { await ConnectAsync(); return; }
        await RunAsync(async () =>
        {
            StatusText.Text = "Refreshing workspace…";
            CancelReading();
            var next = await service.LoadAsync(lifetime.Token);
            var nextAssigned = await service.LoadAssignedAsync(lifetime.Token);
            snapshot = next; assignedQueue = nextAssigned; readingDetails = null; CloseInspector(); PopulateFilters(); Render(); SetLoadedStatus();
            ShowNotice(snapshot.DashboardWarning ?? (IsWorkPage && assignedQueue.LimitReached ? "Your assigned-work limit was reached. Increase the limit in Settings to see more." : null) ?? (snapshot.LimitReached ? "Item limit reached. Metrics describe the most recently changed items loaded." : null));
        });
    }
    private void PopulateFilters()
    {
        filtering = true;
        var type = TypeFilter.SelectedItem as string; var owner = OwnerFilter.SelectedItem as string;
        TypeFilter.ItemsSource = new[] { "All types" }.Concat(snapshot.Items.Concat(assignedQueue.Items).Select(i => i.Type).Distinct().Order()).ToArray();
        OwnerFilter.ItemsSource = new[] { "Everyone" }.Concat(snapshot.Items.Concat(assignedQueue.Items).Select(i => i.AssignedTo).Distinct().Order()).ToArray();
        TypeFilter.SelectedItem = TypeFilter.Items.Contains(type) ? type : "All types";
        OwnerFilter.SelectedItem = OwnerFilter.Items.Contains(owner) ? owner : "Everyone";
        filtering = false;
    }
    private IEnumerable<WorkItem> Filtered()
    {
        var query = SearchBox.Text.Trim(); var type = TypeFilter.SelectedItem as string; var owner = OwnerFilter.SelectedItem as string;
        return snapshot.Items.Where(i => (string.IsNullOrEmpty(type) || type == "All types" || i.Type == type) &&
            (string.IsNullOrEmpty(owner) || owner == "Everyone" || i.AssignedTo == owner) &&
            (string.IsNullOrEmpty(query) || $"{i.Id} {i.Title} {i.AssignedTo} {i.Tags}".Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    private void Navigate(object sender, RoutedEventArgs e) => Go((string)((Button)sender).Tag);
    private void Go(string next)
    {
        if (busy || !DiscardDraft()) return;
        if (next != page) { CancelReading(); readingDetails = null; }
        page = next; CloseInspector(); Render();
    }
    private void Render()
    {
        if (!ready) return;
        Breadcrumb.Text = page;
        foreach (var b in new[] { AssignedNav, AllNav, RecentNav, OverviewNav, BoardNav, WorkNav, DashboardNav, SettingsNav })
        { var active = (string)b.Tag == page; b.Background = Brush(active ? "#243D35" : "Transparent"); b.Foreground = Brush(active ? "#85E6C4" : "#93A1B3"); }
        FilterBar.Visibility = IsWorkPage || page is "Board" or "Work items" ? Visibility.Visible : Visibility.Collapsed;
        OwnerFilter.Visibility = IsWorkPage ? Visibility.Collapsed : Visibility.Visible;
        FilterBar.ColumnDefinitions[2].Width = new GridLength(IsWorkPage ? 0 : 160);
        NewButton.Visibility = page == "Settings" ? Visibility.Collapsed : Visibility.Visible;
        (PageTitle.Text, PageSubtitle.Text) = page switch {
            "Assigned to me" => ("Assigned to me", "Your work queue · descriptions, discussion, and linked work."),
            "All work" => ("All work", "Recent project work · select an item to read it."),
            "Recently viewed" => ("Recently viewed", "Items opened during this connection session."),
            "Board" => ("Board", "Drag cards to move work forward. Select a card for the details."),
            "Work items" => ("Work items", "Search, sort, and filter your work. Double-click a row to open the editor."),
            "Dashboards" => ("Dashboards", "Your Azure DevOps dashboards, a click away."),
            "Settings" => ("Connection settings", "A project URL and a token. Then you're home."),
            _ => ("A clear view of what’s next.", "A native overview of your loaded work items, without the noise.") };
        PageContent.Content = page switch { "Assigned to me" or "All work" or "Recently viewed" => BuildInbox(), "Board" => BuildBoard(), "Work items" => BuildWorkItems(), "Dashboards" => BuildDashboards(), "Settings" => BuildSettings(), _ => BuildOverview() };
        ApplyFontScale();
    }

    private void ApplyFontScale()
    {
        foreach (var element in VisualTree(this))
        {
            if (element is not (Control or TextBlock or Window)) continue;
            var current = (double)element.GetValue(TextElement.FontSizeProperty);
            if (current <= 0 || double.IsNaN(current) || double.IsInfinity(current)) continue;
            var baseline = (double)element.GetValue(BaseFontSizeProperty);
            if (baseline <= 0) { baseline = current; element.SetValue(BaseFontSizeProperty, baseline); }
            element.SetCurrentValue(TextElement.FontSizeProperty, UiAppearance.Size(baseline));
            if (element is Control control) control.FontFamily = new FontFamily(UiAppearance.FontFamilyName);
            else if (element is TextBlock textBlock) textBlock.FontFamily = new FontFamily(UiAppearance.FontFamilyName);
        }
    }

    private static IEnumerable<DependencyObject> VisualTree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualTree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private void FilterChanged(object sender, RoutedEventArgs e) { if (ready && !filtering) { if (IsWorkPage) UpdateQueueList(); else if (page is "Board" or "Work items") Render(); } }
    private void RefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void NewClick(object sender, RoutedEventArgs e) => ShowNew();

    private UIElement BuildOverview()
    {
        var root = new StackPanel();
        var items = snapshot.Items; var completed = items.Count(i => i.Category == "Completed");
        var metricGrid = new Grid { Margin = new(0, 0, 0, 22) };
        var metrics = new[] { ("LOADED WORK ITEMS", items.Count.ToString(), "A focused slice of your project", "#AABFFF"), ("IN PROGRESS", items.Count(i => i.Category == "InProgress").ToString(), "Work currently moving forward", "#77E0BC"), ("OPEN BUGS", items.Count(i => i.Type == "Bug" && i.Category is not ("Completed" or "Removed")).ToString(), "Small fixes, meaningful impact", "#F0808D"), ("COMPLETED", completed.ToString(), "Finished in the loaded work set", "#EAC17B") };
        for (var n = 0; n < metrics.Length; n++)
        {
            metricGrid.ColumnDefinitions.Add(new()); var m = metrics[n];
            var number = Text(m.Item2, 36, m.Item4, true); number.Margin = new(0, 12, 0, 9);
            var c = Card(Stack(Text(m.Item1, 10, "#A3B0C0", true), number, Text(m.Item3, 11, "#899AAF"))); c.Margin = new(n == 0 ? 0 : 7, 0, n == 3 ? 0 : 7, 0); Grid.SetColumn(c, n); metricGrid.Children.Add(c);
        }
        root.Children.Add(metricGrid);
        var center = new Grid(); center.ColumnDefinitions.Add(new() { Width = new(1.25, GridUnitType.Star) }); center.ColumnDefinitions.Add(new());
        var flow = Stack(Text("Delivery at a glance", 17, bold: true), Text("State categories across the loaded work set", 11, "#93A1B3"));
        foreach (var (category, name, color) in Categories())
        {
            var count = items.Count(i => i.Category == category); var caption = new Grid { Margin = new(0, 19, 0, 8) };
            caption.Children.Add(Text(name, 12, "#BAC6D5")); var val = Text(count.ToString(), 12, color, true); val.HorizontalAlignment = HorizontalAlignment.Right; caption.Children.Add(val); flow.Children.Add(caption);
            var bar = new Grid { Height = 6, Background = Brush("#293543") }; bar.ColumnDefinitions.Add(new() { Width = new(Math.Max(count, .01), GridUnitType.Star) }); bar.ColumnDefinitions.Add(new() { Width = new(Math.Max(items.Count - count, .01), GridUnitType.Star) }); bar.Children.Add(new Border { Background = Brush(color), CornerRadius = new(3) }); flow.Children.Add(bar);
        }
        var fc = Card(flow); fc.Margin = new(0, 0, 10, 0); center.Children.Add(fc);
        var focus = Stack(Text("Keep the important things close.", 20, bold: true), Text("A workspace that puts the work first.", 12, "#A8BDB7"));
        focus.Children[1].SetValue(MarginProperty, new Thickness(0, 12, 0, 20));
        var open = ActionButton("Open the board  →", () => Go("Board"), true); focus.Children.Add(open);
        var owned = items.Count(i => i.AssignedTo == "Unassigned" && i.Category != "Completed");
        focus.Children.Add(Label("Attention needed")); focus.Children.Add(Text($"{owned} unassigned open items", 15, "#BBE5D4", true));
        var note = Text("Assign ownership in Azure DevOps to give the next step a clear home.", 12, "#9DB5AC"); note.Margin = new(0, 10, 0, 0); focus.Children.Add(note);
        var focusCard = Card(focus, "#1A302B"); Grid.SetColumn(focusCard, 1); focusCard.Margin = new(10, 0, 0, 0); center.Children.Add(focusCard); root.Children.Add(center);
        var recentHeader = new Grid { Margin = new(0, 25, 0, 14) }; recentHeader.Children.Add(Text("Recently touched", 17, bold: true));
        var all = ActionButton("View all work  →", () => Go("Work items")); all.HorizontalAlignment = HorizontalAlignment.Right; recentHeader.Children.Add(all); root.Children.Add(recentHeader);
        var recent = new StackPanel(); foreach (var i in items.OrderByDescending(i => i.ChangedAt).Take(5)) recent.Children.Add(RecentRow(i));
        if (items.Count == 0) recent.Children.Add(Text("A clean slate. Create your first work item to get started.", 14, "#93A1B3"));
        root.Children.Add(Card(recent, padding: new Thickness(16, 6, 16, 6)));
        return new ScrollViewer { Content = root };
    }
    private UIElement RecentRow(WorkItem item)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(74) }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new(110) });
        grid.Children.Add(Text("#" + item.Id, 12, item.KindColor)); var title = Text(item.Title, 13); title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap; Grid.SetColumn(title, 1); grid.Children.Add(title);
        var state = Text(item.State, 11, "#A5B9CB"); state.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(state, 2); grid.Children.Add(state);
        var button = new Button { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(6, 13, 6, 13), ToolTip = "Open work item" }; button.Click += (_, _) => ShowItem(item); return button;
    }
    private static IEnumerable<(string Category, string Name, string Color)> Categories()
    { yield return ("Proposed", "Ready to start", "#AABFFF"); yield return ("InProgress", "In progress", "#77E0BC"); yield return ("Resolved", "In review / resolved", "#EAC17B"); yield return ("Completed", "Completed", "#8197AE"); }
    private UIElement BuildBoard()
    {
        var items = Filtered().ToArray(); var columns = Categories().ToList();
        foreach (var unknown in items.Select(i => i.Category).Distinct().Where(c => !columns.Any(x => x.Category == c))) columns.Add((unknown, unknown, "#B69CFF"));
        var grid = new Grid();
        for (var n = 0; n < columns.Count; n++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new(Math.Max(220, ((PageContent.ActualWidth > 0 ? PageContent.ActualWidth : ActualWidth - 278) - columns.Count * 12) / columns.Count)) }); var c = columns[n]; var group = items.Where(i => i.Category == c.Category).ToArray();
            var panel = new DockPanel(); var heading = Stack(Text("●  " + c.Name + "   " + group.Length, 13, c.Color, true)); heading.Margin = new(0, 0, 0, 18); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            var cards = new StackPanel(); foreach (var item in group) cards.Children.Add(BoardCard(item));
            if (group.Length == 0) cards.Children.Add(Card(Text("Room for what's next.", 12, "#6F8298"), "#141C26", new(14)));
            panel.Children.Add(new ScrollViewer { Content = cards }); var column = new Border { Background = Brush("#141C26"), CornerRadius = new(9), Padding = new(12), Margin = new(n == 0 ? 0 : 6, 0, n == columns.Count - 1 ? 0 : 6, 0), Child = panel };
            column.AllowDrop = true;
            column.DragOver += (_, e) => { e.Effects = !busy && e.Data.GetDataPresent(typeof(WorkItem)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            column.Drop += async (_, e) => { if (e.Data.GetData(typeof(WorkItem)) is WorkItem dropped) await MoveItemAsync(dropped, c.Category); e.Handled = true; };
            Grid.SetColumn(column, n); grid.Children.Add(column);
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private UIElement BoardCard(WorkItem item)
    {
        var title = Text(item.Title, 14, bold: true); title.Margin = new(0, 12, 0, 12);
        var state = Text(item.State + "   ·   P" + item.Priority, 10, "#96A8BD");
        var tags = Text(item.Tags.Replace(";", "  · "), 10, "#9CB3A8"); tags.Margin = new(0, 10, 0, 15);
        var foot = new Grid(); foot.Children.Add(Text(item.Initials + "  " + item.AssignedTo, 10, "#A6B7CA")); var points = Text(item.Points.ToString("0.#") + " pt", 10, "#8498B0"); points.HorizontalAlignment = HorizontalAlignment.Right; foot.Children.Add(points);
        var content = Stack(Text(item.Type.ToUpperInvariant() + "   #" + item.Id, 9, item.KindColor, true), title, state, tags, foot);
        var b = new Button { Content = content, Background = Brush("#1B2633"), BorderBrush = Brush("#2D3B4C"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(14), Margin = new(0, 0, 0, 11) }; Point origin = default; bool dragged = false;
        b.PreviewMouseLeftButtonDown += (_, e) => { origin = e.GetPosition(b); dragged = false; };
        b.PreviewMouseMove += (_, e) =>
        {
            if (busy || e.LeftButton != MouseButtonState.Pressed || dragged) return;
            var position = e.GetPosition(b);
            if (Math.Abs(position.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragged = true; DragDrop.DoDragDrop(b, new DataObject(typeof(WorkItem), item), DragDropEffects.Move);
        };
        b.Click += (_, _) => { if (!dragged) ShowItem(item); }; return b;
    }
    private async Task MoveItemAsync(WorkItem item, string category)
    {
        if (busy || service == null || item.Category == category || !DiscardDraft()) return;
        var choices = snapshot.Types.FirstOrDefault(t => t.Name == item.Type)?.States.Where(s => s.Category == category).ToArray() ?? [];
        if (choices.Length != 1)
        {
            hasDraft = null; ShowItem(item); ShowNotice(choices.Length == 0 ? "This work item type has no state in that category." : "This category has multiple states. Choose the exact state in the editor and save."); return;
        }
        await RunAsync(async () =>
        {
            var updated = await service.UpdateAsync(item, new(item.Title, choices[0].Name, item.Tags, item.Priority), lifetime.Token);
            UpdateKnownItem(updated);
            CloseInspector(); Render(); SetLoadedStatus(); ShowNotice($"#{item.Id} moved to {updated.State}." + (settings.UseDemoData ? " Demo session only." : " Saved to Azure DevOps."));
        });
    }
    private UIElement BuildWorkItems()
    {
        var grid = new RadGridView { ItemsSource = Filtered().ToArray(), AutoGenerateColumns = false, IsReadOnly = true, ShowGroupPanel = false, RowIndicatorVisibility = Visibility.Collapsed, Background = Brush("#19222E"), Foreground = Brush("#EAF0F6"), BorderBrush = Brush("#293341"), RowHeight = 47, FontSize = 13, SelectionMode = SelectionMode.Single, CanUserFreezeColumns = false };
        var rowStyle = new Style(typeof(Telerik.Windows.Controls.GridView.GridViewRow)); rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#19222E"))); rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#EAF0F6"))); grid.RowStyle = rowStyle;
        var headerStyle = new Style(typeof(Telerik.Windows.Controls.GridView.GridViewHeaderCell)); headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#243142"))); headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#A8B9CD")));
        grid.Resources.Add(typeof(Telerik.Windows.Controls.GridView.GridViewHeaderCell), headerStyle);
        void Add(string field, string header, GridViewLength width) => grid.Columns.Add(new GridViewDataColumn { DataMemberBinding = new Binding(field), Header = header, Width = width });
        Add("Id", "ID", new(70)); Add("Type", "TYPE", new(110)); Add("Title", "TITLE", new(1, GridViewLengthUnitType.Star)); Add("State", "STATE", new(100)); Add("AssignedTo", "ASSIGNED TO", new(135)); Add("Priority", "PRIORITY", new(80)); Add("ShortIteration", "ITERATION", new(110));
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is WorkItem item) ShowItem(item); };
        grid.KeyDown += (_, e) => { if (e.Key == Key.Enter && grid.SelectedItem is WorkItem item) { ShowItem(item); e.Handled = true; } };
        return grid;
    }
    private UIElement BuildDashboards()
    {
        var list = new StackPanel();
        var note = Card(Stack(Text("A native view. A familiar connection.", 18, bold: true), Text("The Overview is built for this desktop client. Azure dashboard widgets run on the web; open a saved dashboard below to use its original widgets.", 13, "#A6B7CA")), "#1A302B"); note.Margin = new(0, 0, 0, 22); list.Children.Add(note);
        foreach (var d in snapshot.Dashboards)
        {
            var content = Stack(Text(d.Name, 19, bold: true), Text(string.IsNullOrWhiteSpace(d.Description) ? "Azure DevOps dashboard" : d.Description, 13, "#93A1B3")); content.Children[1].SetValue(MarginProperty, new Thickness(0, 10, 0, 18));
            if (!string.IsNullOrEmpty(d.Url)) content.Children.Add(ActionButton("Open in Azure DevOps  ↗", () => OpenUrl(d.Url)));
            else content.Children.Add(Text("DEMO · Connect your project in Settings", 10, "#77E0BC", true));
            var card = Card(content); card.Margin = new(0, 0, 0, 14); list.Children.Add(card);
        }
        if (snapshot.Dashboards.Count == 0) list.Children.Add(Text("No dashboards returned. Check the team setting and Dashboard read permissions.", 14, "#93A1B3"));
        return new ScrollViewer { Content = list };
    }
    private UIElement BuildSettings()
    {
        var form = new StackPanel { MaxWidth = 740, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Card(Stack(Text("Connection settings", 18, bold: true), Text("Saved to appsettings.local.json beside the executable. This file overrides appsettings.json.", 12, "#93A1B3"))));
        var url = new TextBox { Text = settings.ProjectUrl, MinWidth = 610, ToolTip = "https://dev.azure.com/organization/project" };
        var pat = new PasswordBox { Password = settings.PersonalAccessToken };
        var team = new TextBox { Text = settings.Team };
        var limit = new TextBox { Text = settings.MaxWorkItems.ToString() };
        var demo = new CheckBox { Content = "Use the demo workspace", IsChecked = settings.UseDemoData };
        var theme = new RadComboBox { ItemsSource = new[] { "Dark", "Light", "High contrast" }, SelectedItem = settings.Theme };
        var fontFamily = new RadComboBox { ItemsSource = new[] { "Segoe UI", "Arial", "Calibri", "Tahoma", "Verdana" }, SelectedItem = settings.FontFamily };
        var fontScale = new RadComboBox { ItemsSource = new[] { "80%", "90%", "100%", "115%", "135%", "155%", "180%" }, SelectedItem = settings.FontScale switch { <= .8 => "80%", <= .9 => "90%", <= 1.0 => "100%", <= 1.15 => "115%", <= 1.35 => "135%", <= 1.55 => "155%", _ => "180%" } };
        form.Children.Add(Label("Project URL")); form.Children.Add(url);
        form.Children.Add(Text("https://dev.azure.com/organization/project", 11, "#71859C"));
        form.Children.Add(Label("Personal access token")); form.Children.Add(pat);
        var warning = Text("The PAT is stored as plain text in your local settings file. Keep it private. Work Items: Read & write enables editing; Dashboard: Read enables dashboard discovery.", 12, "#BFAE87"); warning.Margin = new(0, 9, 0, 0); form.Children.Add(warning);
        form.Children.Add(Label("Team (optional · for dashboard discovery)")); form.Children.Add(team);
        form.Children.Add(Label("Recent work item limit · 1 to 2000")); form.Children.Add(limit);
        form.Children.Add(demo);
        form.Children.Add(Text("Demo changes last for the current session. Live mode reads and writes directly to Azure DevOps. Boards summarize the project’s loaded work by state category; team swimlanes and WIP limits are not yet synchronized.", 12, "#93A1B3"));
        form.Children.Add(Label("Accessibility and appearance"));
        form.Children.Add(Text("Choose a theme, font, and reading size. Larger sizes are applied throughout the workspace and the Markdown reader.", 12, "#93A1B3"));
        form.Children.Add(Text("Theme", 12, "#A4B7CC", true)); form.Children.Add(theme);
        form.Children.Add(Text("Font", 12, "#A4B7CC", true)); form.Children.Add(fontFamily);
        form.Children.Add(Text("Reading size", 12, "#A4B7CC", true)); form.Children.Add(fontScale);
        var applyAppearance = ActionButton("Apply appearance", () =>
        {
            try
            {
                var next = settings with { Theme = (string?)theme.SelectedItem ?? "Dark", FontFamily = (string?)fontFamily.SelectedItem ?? "Segoe UI", FontScale = ScaleValue((string?)fontScale.SelectedItem) };
                SettingsStore.Save(AppContext.BaseDirectory, next); settings = next; UiAppearance.Apply(next); Render(); ShowNotice("Appearance saved.");
            }
            catch (ArgumentException ex) { ReportError(ex.Message); }
        });
        applyAppearance.Margin = new(0, 14, 0, 0); form.Children.Add(applyAppearance);
        var save = ActionButton("Save & connect", () => { _ = SaveSettingsAsync(); }, true); save.Margin = new(0, 22, 0, 0); form.Children.Add(save);
        var file = ActionButton("Open settings folder  ↗", () => Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true })); file.Margin = new(0, 14, 0, 0); form.Children.Add(file);
        return new ScrollViewer { Content = form };
        async Task SaveSettingsAsync()
        {
            if (busy) return;
            try
            {
                if (!int.TryParse(limit.Text, out var count)) throw new ArgumentException("Enter a numeric work item limit.");
                var next = new ClientSettings { ProjectUrl = url.Text.Trim(), PersonalAccessToken = pat.Password.Trim(), Team = team.Text.Trim(), MaxWorkItems = count, UseDemoData = demo.IsChecked == true,
                    Theme = (string?)theme.SelectedItem ?? settings.Theme, FontFamily = (string?)fontFamily.SelectedItem ?? settings.FontFamily, FontScale = ScaleValue((string?)fontScale.SelectedItem) };
                SettingsStore.Save(AppContext.BaseDirectory, next);
                CancelReading(); assignedQueue = new([]); readingDetails = null; readingId = null; recentlyViewed.Clear(); recentlyViewedOrder.Clear(); service?.Dispose(); service = null; snapshot = new([], [], []); settings = next;
                ModeLabel.Text = next.UseDemoData ? "●  DEMO WORKSPACE" : "○  NOT CONNECTED";
                ProjectLabel.Text = next.ProjectName; PopulateFilters(); ShowNotice(null);
                await ConnectAsync();
                if (service != null) { page = "Assigned to me"; Render(); }
            }
            catch (ArgumentException ex) { ReportError(ex.Message); }
            catch (Exception) { ShowNotice("Settings could not be saved. Check that the application folder is writable."); }
        }
        static double ScaleValue(string? value) => value switch { "80%" => .8, "90%" => .9, "115%" => 1.15, "135%" => 1.35, "155%" => 1.55, "180%" => 1.8, _ => 1.0 };
    }
    private bool DiscardDraft() => hasDraft?.Invoke() != true || MessageBox.Show(this, "Discard the changes in the editor?", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void CloseInspector() { selected = null; hasDraft = null; Inspector.Children.Clear(); InspectorColumn.Width = new(0); if (readingHost != null) readingHost.IsEnabled = true; }
    private void OpenInspector(string heading)
    {
        Inspector.Children.Clear(); InspectorColumn.Width = new(350); if (readingHost != null) readingHost.IsEnabled = false;
        var row = new Grid(); row.Children.Add(Text(heading, 11, "#93A1B3", true));
        var close = ActionButton("✕", () => { if (!busy && DiscardDraft()) CloseInspector(); }); close.Padding = new(8, 2, 8, 3); close.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(close); Inspector.Children.Add(row);
    }
    private void ShowItem(WorkItem item)
    {
        if (busy || !DiscardDraft()) return;
        selected = item; hasDraft = null; OpenInspector("WORK ITEM  /  #" + item.Id);
        var badge = Text(item.Type.ToUpperInvariant(), 10, item.KindColor, true); badge.Margin = new(0, 24, 0, 0); Inspector.Children.Add(badge);
        var title = new TextBox { Text = item.Title, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxLength = 255, MinHeight = 76, VerticalContentAlignment = VerticalAlignment.Center, Margin = new(0, 12, 0, 0) }; Inspector.Children.Add(title);
        var states = snapshot.Types.FirstOrDefault(t => t.Name == item.Type)?.States.Select(s => s.Name).ToList() ?? [];
        if (!states.Contains(item.State)) states.Add(item.State);
        var state = new RadComboBox { ItemsSource = states, SelectedItem = item.State };
        var priority = new RadComboBox { ItemsSource = new[] { 1, 2, 3, 4 }, SelectedItem = item.Priority };
        var tags = new TextBox { Text = item.Tags };
        Inspector.Children.Add(Label("State")); Inspector.Children.Add(state);
        Inspector.Children.Add(Label("Priority")); Inspector.Children.Add(priority);
        Inspector.Children.Add(Label("Tags · separated by semicolons")); Inspector.Children.Add(tags);
        Inspector.Children.Add(Label("Assigned to")); Inspector.Children.Add(Text(item.AssignedTo, 13));
        Inspector.Children.Add(Label("Iteration")); Inspector.Children.Add(Text(item.Iteration, 12, "#B1C0D1"));
        Inspector.Children.Add(Label("Description"));
        Inspector.Children.Add(Text(PlainText(item.Description), 12, "#B1C0D1"));
        Inspector.Children.Add(Label("Last changed")); Inspector.Children.Add(Text(item.ChangedAt.ToLocalTime().ToString("dd MMM yyyy · HH:mm") + "  /  revision " + item.Revision, 11, "#8B9FB7"));
        Button save = null!; save = ActionButton(settings.UseDemoData ? "Save demo changes" : "Save to Azure DevOps", () => { _ = SaveAsync(); }, true); save.Margin = new(0, 25, 0, 0); Inspector.Children.Add(save);
        var reset = ActionButton("Discard changes", () => { hasDraft = null; ShowItem(item); }); reset.Margin = new(0, 10, 0, 0); Inspector.Children.Add(reset);
        if (!settings.UseDemoData) { var web = ActionButton("Open in browser  ↗", () => OpenUrl(new Uri(settings.ProjectUri(), "_workitems/edit/" + item.Id).AbsoluteUri)); web.Margin = new(0, 10, 0, 0); Inspector.Children.Add(web); }
        hasDraft = () => title.Text != item.Title || (string?)state.SelectedItem != item.State || tags.Text != item.Tags || (int?)priority.SelectedItem != item.Priority;
        async Task SaveAsync()
        {
            if (busy || service == null) return;
            if (string.IsNullOrWhiteSpace(title.Text)) { ShowNotice("A work item needs a title."); return; }
            save.IsEnabled = false;
            await RunAsync(async () =>
            {
                var updated = await service.UpdateAsync(item, new(title.Text.Trim(), (string)state.SelectedItem, tags.Text.Trim(), (int)priority.SelectedItem), lifetime.Token);
                UpdateKnownItem(updated);
                hasDraft = null; PopulateFilters(); Render(); SetLoadedStatus(); ShowNotice(settings.UseDemoData ? "Saved in this demo session." : $"Work item #{updated.Id} saved to Azure DevOps.");
                // RunAsync still holds the busy flag; open the refreshed editor on the next dispatcher turn.
                _ = Dispatcher.BeginInvoke(new Action(() => ShowItem(updated)));
            });
            save.IsEnabled = true;
        }
    }
    private void ShowNew()
    {
        if (busy || service == null || snapshot.Types.Count == 0 || !DiscardDraft()) return;
        selected = null; hasDraft = null; OpenInspector("CREATE  /  WORK ITEM");
        var heading = Text("Start something good.", 24, bold: true); heading.Margin = new(0, 24, 0, 12); Inspector.Children.Add(heading);
        Inspector.Children.Add(Text(settings.UseDemoData ? "Try it here. Demo changes stay in this session." : "This creates a work item in your connected project.", 12, "#93A1B3"));
        var type = new RadComboBox { ItemsSource = snapshot.Types.Select(t => t.Name).ToArray(), SelectedIndex = 0 };
        var title = new TextBox { MaxLength = 255, MinHeight = 90, TextWrapping = TextWrapping.Wrap };
        Inspector.Children.Add(Label("Work item type")); Inspector.Children.Add(type);
        Inspector.Children.Add(Label("Give it a clear title")); Inspector.Children.Add(title);
        Button create = null!; create = ActionButton(settings.UseDemoData ? "Create demo item" : "Create in Azure DevOps", () => { _ = CreateAsync(); }, true); create.Margin = new(0, 22, 0, 0); Inspector.Children.Add(create);
        hasDraft = () => !string.IsNullOrWhiteSpace(title.Text); title.Focus();
        async Task CreateAsync()
        {
            if (busy || service == null) return;
            if (string.IsNullOrWhiteSpace(title.Text)) { ShowNotice("Enter a title for the new work item."); title.Focus(); return; }
            create.IsEnabled = false;
            await RunAsync(async () =>
            {
                var item = await service.CreateAsync((string)type.SelectedItem, title.Text.Trim(), lifetime.Token);
                snapshot = snapshot with { Items = new[] { item }.Concat(snapshot.Items).ToArray() };
                hasDraft = null; PopulateFilters(); Render(); SetLoadedStatus(); ShowNotice(settings.UseDemoData ? $"Demo item #{item.Id} created for this session." : $"Work item #{item.Id} created in Azure DevOps.");
                _ = Dispatcher.BeginInvoke(new Action(() => ShowItem(item)));
            });
            create.IsEnabled = true;
        }
    }
    private static string PlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "No description yet.";
        var lineBreaks = Regex.Replace(html, @"<(br\s*/?|/p|/div|/li)>", "\n", RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(lineBreaks, "<[^>]+>", "")).Trim();
    }
    private void OpenUrl(string url)
    {
        try { if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { ShowNotice("Windows could not open the browser."); }
    }
    private async Task SmokeAsync()
    {
        try
        {
            var output = Environment.GetEnvironmentVariable("ORBIT_SMOKE_DIR") ?? Path.Combine(AppContext.BaseDirectory, "smoke"); Directory.CreateDirectory(output);
            await SmokeWorkspaceAsync(output);
            foreach (var name in new[] { "Overview", "Board", "Work items", "Dashboards", "Settings" })
            {
                Go(name); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (name == "Settings" && FindVisual<RadComboBox>(PageContent).Count() < 3) throw new Exception("Accessibility settings controls are missing");
                UpdateLayout(); Capture(Path.Combine(output, name.Replace(" ", "-").ToLowerInvariant() + ".png"));
            }
            Go("Overview"); ShowItem(snapshot.Items[0]); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            UpdateLayout(); Capture(Path.Combine(output, "editor.png"));
            CloseInspector(); Go("Work items");
            SearchBox.Text = "1041"; if (Filtered().Count() != 1) throw new Exception("Search failed");
            SearchBox.Text = ""; TypeFilter.SelectedItem = "Bug"; if (Filtered().Any(i => i.Type != "Bug")) throw new Exception("Type filter failed");
            TypeFilter.SelectedItem = "All types"; OwnerFilter.SelectedItem = "Alex Morgan"; if (Filtered().Any(i => i.AssignedTo != "Alex Morgan")) throw new Exception("Owner filter failed");
            OwnerFilter.SelectedItem = "Everyone";
            ShowNew(); var titleBox = Inspector.Children.OfType<TextBox>().Single(); titleBox.Text = "Smoke-created work item";
            Inspector.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var created = snapshot.Items.Single(i => i.Title == "Smoke-created work item");
            Inspector.Children.OfType<TextBox>().First().Text = "Smoke-updated work item";
            Inspector.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (snapshot.Items.Single(i => i.Id == created.Id).Title != "Smoke-updated work item") throw new Exception("Editor save failed");
            await RefreshAsync(); if (snapshot.Items.All(i => i.Title != "Smoke-updated work item")) throw new Exception("Refresh lost saved changes");
            await MoveItemAsync(snapshot.Items.Single(i => i.Id == created.Id), "InProgress"); if (snapshot.Items.Single(i => i.Id == created.Id).Category != "InProgress") throw new Exception("Board move failed");
            Width = 1100; Height = 740; Go("Board"); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); UpdateLayout(); Capture(Path.Combine(output, "board-compact.png"));
            File.WriteAllText(Path.Combine(output, "smoke.txt"), "PASS: assigned queue, native description, paged comments, parent/child links, status saves, recent view, compact reader and stale selection protection; six secondary screens rendered; search, type/owner filters, create, editor save, refresh, board state move, and compact board verified. Always uses demo data.");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), ex.ToString()); hasDraft = null; Application.Current.Shutdown(1); }
        finally { hasDraft = null; Close(); }
    }
    private void Capture(string path)
    {
        var target = (FrameworkElement)Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(target);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); png.Save(stream);
    }
}













