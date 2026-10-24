using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DevOpsClient.Core;
using Telerik.Windows.Controls;

namespace DevOpsClient;
public partial class MainWindow
{
    private async Task SmokeWorkspaceAsync(string output)
    {
        if (!settings.UseDemoData) throw new Exception("Workspace smoke must never use live data.");
        Go("Assigned to me");
        readingTask = SelectReadingAsync(1041, false); await readingTask;
        if (readingDetails?.Item.Id != 1041 || !QueueItems().All(i => i.AssignedTo == "Alex Morgan")) throw new Exception("Assigned queue failed");
        await IdleCapture("assigned-work");
        var headerState = FindVisual<RadComboBox>(readingHost!).Single(); headerState.SelectedItem = "Active";
        if (hasDraft?.Invoke() != true) throw new Exception("Reading state draft not tracked");
        var tabs = FindVisual<TabControl>(readingHost!).Single(); tabs.SelectedIndex = 1;
        UpdateLayout(); FindVisual<Button>(commentsHost!).Single(b => b.Content is string text && text == "Load more comments").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await readingTask;
        if (readingComments.Count != 3 || commentsNext != null || hasDraft?.Invoke() != true) throw new Exception("Comment paging lost state draft");
        // Return to the original state before navigating, without invoking a confirmation dialog.
        headerState.SelectedItem = "New";
        await IdleCapture("work-comments");
        tabs.SelectedIndex = 2; await IdleCapture("work-relations");
        var parent = readingDetails!.Relations.Single(r => r.Relationship == "Parent");
        FindVisual<Button>(readingHost!).First(b => b.ToolTip is string tip && tip == "Read linked work item #" + parent.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await readingTask;
        if (readingDetails!.Relations.All(r => r.Id != 1041)) throw new Exception("Parent/child navigation failed");
        UpdateLayout(); FindVisual<Button>(readingHost!).Single(b => b.Content is string text && text == "← Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await readingTask;
        if (readingDetails!.Item.Id != 1041) throw new Exception("Reading back navigation failed"); SearchBox.Text = "nothing matches this text"; if (QueueItems().Any()) throw new Exception("Queue search failed"); SearchBox.Text = ""; UpdateLayout();
        var state = FindVisual<RadComboBox>(readingHost!).Single(); state.SelectedItem = "Active";
        FindVisual<Button>(readingHost!).Single(b => b.Content is string text && text == "Save status").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (readingDetails!.Item.State != "Active" || assignedQueue.Items.Single(i => i.Id == 1041).State != "Active") throw new Exception("Reading status save failed");
        // Simulate a slow previous selection returning after a later one, even when the server ignores cancellation.
        var original = service!; service = new DelayedReadingService(original);
        try
        {
            var slow = SelectReadingAsync(1050); var fast = SelectReadingAsync(1046); await Task.WhenAll(slow, fast);
            if (readingDetails!.Item.Id != 1046) throw new Exception("Stale selection replaced the current item");
        }
        finally { service = original; }
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = OwnedWindows.Cast<Window>().Single(); dialog.UpdateLayout();
            FindVisual<TextBox>(dialog).Single().Text = "#1050";
            FindVisual<Button>(dialog).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }), System.Windows.Threading.DispatcherPriority.Background);
        ShowOpenByNumber(); await readingTask;
        if (readingDetails!.Item.Id != 1050) throw new Exception("Open by number failed");
        var document = ReadableDocument.Create("<h2>Heading</h2><script>unsafe()</script><pre>&lt;value&gt;</pre>");
        var textContent = new TextRange(document.Document.ContentStart, document.Document.ContentEnd).Text;
        if (textContent.Contains("unsafe") || !textContent.Contains("<value>")) throw new Exception("Document sanitization failed");
        var markdown = ReadableDocument.Create("# Markdown heading\n\n**bold** and *italic* with [a link](https://example.com)\n\n> a quoted note\n\n- one\n- two\n\n```csharp\nvar answer = 42;\n```\n\n[blocked](javascript:alert(1))", false);
        var markdownText = new TextRange(markdown.Document.ContentStart, markdown.Document.ContentEnd).Text;
        if (!markdownText.Contains("Markdown heading") || !markdownText.Contains("bold") || !markdownText.Contains("italic") || !markdownText.Contains("one") || !markdownText.Contains("var answer = 42") || markdownText.Contains("**") || markdownText.Contains("```")) throw new Exception("Markdown rendering failed");
        await SelectReadingAsync(1041); Width = 1100; Height = 740; await IdleCapture("assigned-work-compact"); Width = 1480; Height = 940;
        hasDraft = null; Go("Recently viewed"); if (!QueueItems().Any()) throw new Exception("Recent view is empty");
        hasDraft = null; Go("Overview");
        async Task IdleCapture(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); UpdateLayout(); Capture(Path.Combine(output, name + ".png"));
        }
    }
    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var n = 0; n < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); n++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, n); if (child is T match) yield return match;
            foreach (var nested in FindVisual<T>(child)) yield return nested;
        }
    }
    private sealed class DelayedReadingService(IProjectService inner) : IProjectService
    {
        public Task<ProjectSnapshot> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public Task<WorkQueue> LoadAssignedAsync(CancellationToken token) => inner.LoadAssignedAsync(token);
        public async Task<WorkItemDetails> GetDetailsAsync(int id, CancellationToken token) { if (id == 1050) await Task.Delay(150); return await inner.GetDetailsAsync(id, CancellationToken.None); }
        public Task<CommentPage> GetCommentsAsync(int id, string? next, CancellationToken token) => inner.GetCommentsAsync(id, next, token);
        public Task<WorkItem> UpdateAsync(WorkItem item, WorkItemEdit edit, CancellationToken token) => inner.UpdateAsync(item, edit, token);
        public Task<WorkItem> CreateAsync(string type, string title, CancellationToken token) => inner.CreateAsync(type, title, token);
        public void Dispose() { }
    }
}


