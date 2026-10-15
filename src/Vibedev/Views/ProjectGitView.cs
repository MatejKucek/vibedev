using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vibedev.Core;

namespace Vibedev.Views;

public sealed class ProjectGitView : UserControl, IDisposable
{
    private readonly MainWindow main;
    private readonly ProjectView owner;
    private readonly ListBox staged = new() { SelectionMode = SelectionMode.Extended };
    private readonly ListBox changes = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBlock status = Ui.Text("", 12, "#D4D4DC");
    private readonly TextBlock activity = Ui.Text("", 11, "#9898A6");
    private readonly TextBox message = new() { ToolTip = "Commit message", Margin = new(6), MinHeight = 32 };
    private readonly Grid workingView = new();
    private readonly Grid historyView = new() { Visibility = Visibility.Collapsed };
    private readonly TreeView commits = new() { Padding = new(4, 4, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock historyStatus = Ui.Text("", 11, "#9898A6");
    private readonly Button changesMode, historyMode;
    private readonly CancellationTokenSource lifetime = new();
    private bool refreshing, disposed, showingHistory;
    private Task? refreshTask, historyTask;
    internal IReadOnlyList<GitFileChange> Files { get; private set; } = [];
    internal ListBox ChangesList => changes;
    internal ListBox StagedList => staged;

    public ProjectGitView(MainWindow main, ProjectView owner)
    {
        this.main = main; this.owner = owner;
        var shell = new DockPanel();
        changesMode = Ui.Button("Changes", async () => await SetHistoryAsync(false));
        historyMode = Ui.Button("Recent commits", async () => await SetHistoryAsync(true));
        var modes = Ui.Row(changesMode, historyMode); modes.Margin = new(6, 6, 0, 2);
        foreach (Button button in modes.Children) button.Padding = new(7, 4, 7, 4);
        DockPanel.SetDock(modes, Dock.Top); shell.Children.Add(modes);
        var content = new Grid(); content.Children.Add(workingView); content.Children.Add(historyView); shell.Children.Add(content);
        var root = workingView;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions[3].Height = GridLength.Auto; staged.MaxHeight = 200;
        ScrollViewer.SetHorizontalScrollBarVisibility(staged, ScrollBarVisibility.Disabled); ScrollViewer.SetHorizontalScrollBarVisibility(changes, ScrollBarVisibility.Disabled);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var tools = new StackPanel { Margin = new(6) };
        status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; tools.Children.Add(status);
        activity.Margin = new(0, 3, 0, 0); activity.Visibility = Visibility.Collapsed; tools.Children.Add(activity);
        var actions = Ui.Row(Ui.Button("↓ Pull", () => main.GitAction(owner.Project, "pull")), Ui.Button("↑ Push", () => main.GitAction(owner.Project, "push")), Ui.Button("↻", async () => await RefreshAsync()));
        foreach (Button button in actions.Children) button.Padding = new(7, 4, 7, 4);
        ((Button)actions.Children[2]).ToolTip = "Refresh Git changes"; actions.Margin = new(0, 6, 0, 0); tools.Children.Add(actions); root.Children.Add(tools);
        var commit = new StackPanel(); var messageLabel = Ui.Text("Commit message", 11, "#9898A6"); messageLabel.Margin = new(6, 2, 6, 0); commit.Children.Add(messageLabel); commit.Children.Add(message);
        var syncButton = Ui.Button("Commit all, pull & push", async () => await CommitAllAndSyncAsync(), true); syncButton.Margin = new(6, 0, 6, 4); syncButton.ToolTip = "Save open files, stage all changes, commit with the message above, pull, then push. Stop if any step fails."; commit.Children.Add(syncButton);
        var commitButton = Ui.Button("✓ Commit staged", async () => await CommitAsync()); commitButton.Margin = new(6, 0, 6, 8); commitButton.ToolTip = "Commit only staged changes using the message above"; commit.Children.Add(commitButton); Grid.SetRow(commit, 1); root.Children.Add(commit);
        var unstage = Ui.Button("− Selected", async () => await ChangeStageAsync(true)); unstage.ToolTip = "Unstage selected files; keep their working copies";
        var stagedHeader = SectionHeader("STAGED", unstage);
        Grid.SetRow(stagedHeader, 2); root.Children.Add(stagedHeader); Grid.SetRow(staged, 3); root.Children.Add(staged);
        var stageSelected = Ui.Button("+ Selected", async () => await ChangeStageAsync(false)); stageSelected.ToolTip = "Save and stage selected files (Ctrl/Shift+click for multiple files)";
        var stageAll = Ui.Button("+ All", async () => await ChangeStageAsync(false, true)); stageAll.ToolTip = "Save and stage all changed files";
        var changesHeader = SectionHeader("CHANGES", stageSelected, stageAll);
        Grid.SetRow(changesHeader, 4); root.Children.Add(changesHeader); Grid.SetRow(changes, 5); root.Children.Add(changes);
        staged.SelectionChanged += async (_, _) => await ShowSelectionAsync(staged, true);
        changes.SelectionChanged += async (_, _) => await ShowSelectionAsync(changes, false);
        staged.ToolTip = changes.ToolTip = "Click to review diff. Ctrl/Shift+click to select multiple files.";
        historyView.RowDefinitions.Add(new() { Height = GridLength.Auto }); historyView.RowDefinitions.Add(new());
        var historyTools = new DockPanel { Margin = new(8, 6, 6, 6) };
        var refreshHistory = Ui.Button("↻", async () => await RefreshHistoryAsync()); refreshHistory.Padding = new(7, 4, 7, 4); refreshHistory.ToolTip = "Refresh recent commits";
        DockPanel.SetDock(refreshHistory, Dock.Right); historyTools.Children.Add(refreshHistory); historyTools.Children.Add(Ui.Text("Latest 100 commits", 11, "#9898A6")); historyView.Children.Add(historyTools);
        ScrollViewer.SetHorizontalScrollBarVisibility(commits, ScrollBarVisibility.Disabled);
        Grid.SetRow(commits, 1); historyView.Children.Add(commits);
        historyStatus.Margin = new(12, 8, 12, 8); historyStatus.Visibility = Visibility.Collapsed; Grid.SetRow(historyStatus, 1); historyView.Children.Add(historyStatus);
        commits.KeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter || commits.SelectedItem is not TreeViewItem item) return;
            if (item.Tag is GitCommit) { e.Handled = true; item.IsExpanded = !item.IsExpanded; }
            else if (item.Tag is GitCommitFile file && ItemsControl.ItemsControlFromItemContainer(item) is TreeViewItem { Tag: GitCommit commit })
            {
                e.Handled = true; await ShowCommitDiffAsync(commit, file);
            }
        };
        UpdateMode(); Content = shell;
    }
    // Buttons dock right first so the label trims instead of overlapping them in a narrow sidebar.
    private static DockPanel SectionHeader(string title, params Button[] buttons)
    {
        var header = new DockPanel { Margin = new(8, 8, 6, 4), LastChildFill = true };
        var actions = Ui.Row(buttons); DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        foreach (var button in buttons) { button.Padding = new(7, 3, 7, 3); button.Margin = new(4, 0, 0, 0); }
        var label = Ui.Section(title); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; header.Children.Add(label);
        return header;
    }
    private async Task SetHistoryAsync(bool show)
    {
        showingHistory = show; UpdateMode();
        if (show) await RefreshHistoryAsync(); else await RefreshAsync();
    }
    private void UpdateMode()
    {
        workingView.Visibility = showingHistory ? Visibility.Collapsed : Visibility.Visible;
        historyView.Visibility = showingHistory ? Visibility.Visible : Visibility.Collapsed;
        changesMode.Background = Ui.Brush(showingHistory ? "#2A2A30" : "#343044");
        historyMode.Background = Ui.Brush(showingHistory ? "#343044" : "#2A2A30");
        changesMode.BorderBrush = Ui.Brush(showingHistory ? "#3C3C44" : "#A78BFA");
        historyMode.BorderBrush = Ui.Brush(showingHistory ? "#A78BFA" : "#3C3C44");
    }
    private Task RefreshHistoryAsync()
    {
        if (disposed) return Task.CompletedTask;
        return historyTask is { IsCompleted: false } ? historyTask : historyTask = RefreshHistoryCoreAsync();
    }
    private async Task RefreshHistoryCoreAsync()
    {
        if (commits.Items.Count == 0) { historyStatus.Text = "Loading commits…"; historyStatus.Visibility = Visibility.Visible; }
        try
        {
            var recent = await main.Git.RecentCommitsAsync(owner.Project.Entry.Path, cancellation: lifetime.Token);
            if (disposed) return;
            var existing = commits.Items.OfType<TreeViewItem>().ToDictionary(item => ((GitCommit)item.Tag).Hash);
            if (!recent.Select(commit => commit.Hash).SequenceEqual(commits.Items.OfType<TreeViewItem>().Select(item => ((GitCommit)item.Tag).Hash)))
            {
                var selected = commits.SelectedItem as TreeViewItem;
                commits.Items.Clear();
                foreach (var commit in recent) commits.Items.Add(existing.TryGetValue(commit.Hash, out var item) ? item : CommitNode(commit));
                if (selected is not null) selected.IsSelected = true;
            }
            historyStatus.Text = "No commits yet."; historyStatus.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (disposed) return;
            commits.Items.Clear(); historyStatus.Text = "Cannot load commits: " + ex.Message; historyStatus.Visibility = Visibility.Visible;
        }
    }
    private TreeViewItem CommitNode(GitCommit commit)
    {
        var label = Ui.Text($"{commit.ShortHash}  {commit.Subject}", 11); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
        var item = new TreeViewItem { Tag = commit, Header = label, ToolTip = $"{commit.Subject}\n{commit.Author} · {commit.Date.LocalDateTime:g}\n{commit.Hash}", Style = (Style)Application.Current.FindResource("ProjectTreeItem") };
        label.MouseLeftButtonUp += (_, e) => { e.Handled = true; item.IsSelected = true; item.IsExpanded = !item.IsExpanded; };
        item.Items.Add(Ui.Text("Loading files…", 11, "#9898A6"));
        var loaded = false; var loading = false;
        item.Expanded += async (_, e) =>
        {
            if (e.OriginalSource != item || loaded || loading || disposed) return;
            loading = true;
            try
            {
                var files = await main.Git.CommitFilesAsync(owner.Project.Entry.Path, commit.Hash, lifetime.Token);
                if (disposed) return;
                item.Items.Clear();
                foreach (var file in files)
                {
                    var text = file.OriginalPath is null ? file.Path : $"{file.OriginalPath} → {file.Path}";
                    var fileLabel = Ui.Text($"{file.Status}  {text}", 11); fileLabel.TextWrapping = TextWrapping.NoWrap; fileLabel.TextTrimming = TextTrimming.CharacterEllipsis;
                    var fileItem = new TreeViewItem { Tag = file, Header = fileLabel, ToolTip = text + "\nDouble-click to compare this commit with its parent", Style = (Style)Application.Current.FindResource("ProjectTreeItem") };
                    fileItem.MouseDoubleClick += async (_, e) =>
                    {
                        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
                        e.Handled = true; await ShowCommitDiffAsync(commit, file);
                    };
                    item.Items.Add(fileItem);
                }
                if (files.Count == 0) item.Items.Add(Ui.Text("No changed files.", 11, "#9898A6"));
                loaded = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (disposed) return;
                item.Items.Clear(); item.Items.Add(Ui.Text("Cannot load files. Collapse and expand to retry.", 11, "#F87171")); item.ToolTip = ex.Message;
            }
            finally { loading = false; }
        };
        return item;
    }
    private async Task ShowCommitDiffAsync(GitCommit commit, GitCommitFile file)
    {
        if (disposed) return;
        try { await owner.ShowCommitDiffAsync(commit, file, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) main.Log("Commit diff: " + ex.Message); }
    }
    public void RefreshStatus()
    {
        var project = owner.Project;
        status.Text = project.Git?.IsRepository == true ? $"{project.Git.Summary} · {(project.Git.Changes == 1 ? "1 change" : $"{project.Git.Changes} changes")}" : "Local project · Git unavailable";
        status.ToolTip = project.Git?.Error ?? (project.Git?.IsRepository == true ? project.Git.Branch : project.Entry.Path);
        activity.Text = (project.Failed ? "⚠ " : "") + project.Activity;
        activity.Foreground = Ui.Brush(project.Failed ? "#F87171" : "#9898A6");
        activity.Visibility = project.Activity.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = !project.Busy;
    }
    public Task RefreshAsync()
    {
        if (disposed) return Task.CompletedTask;
        return refreshTask is { IsCompleted: false } ? refreshTask : refreshTask = RefreshCoreAsync();
    }
    private async Task RefreshCoreAsync()
    {
        if (disposed || refreshing) return; refreshing = true;
        try
        {
            var files = await main.Git.ChangesAsync(owner.Project.Entry.Path, lifetime.Token);
            if (disposed) return;
            Files = files;
            Populate(staged, files.Where(f => f.Staged), true); Populate(changes, files.Where(f => f.Unstaged), false);
            await owner.RefreshSelectedDiffAsync(files);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) { Files = []; staged.Items.Clear(); changes.Items.Clear(); status.Text = ex.Message; } }
        finally { refreshing = false; }
        RefreshStatus();
        if (showingHistory) await RefreshHistoryAsync();
    }
    private static void Populate(ListBox list, IEnumerable<GitFileChange> files, bool isStaged)
    {
        var selected = list.SelectedItems.OfType<ListBoxItem>().Select(i => ((GitFileChange)i.Tag).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.Items.Clear();
        foreach (var file in files)
        {
            var label = Ui.Text($"{(isStaged ? file.IndexStatus : file.WorkTreeStatus)}  {file.Path}", 11); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            var item = new ListBoxItem { Tag = file, Content = label, Padding = new(8, 5, 8, 5), ToolTip = file.OriginalPath is null ? file.Path : file.OriginalPath + " → " + file.Path };
            list.Items.Add(item); if (selected.Contains(file.Path)) item.IsSelected = true;
        }
    }
    private async Task ShowSelectionAsync(ListBox list, bool isStaged)
    {
        if (refreshing || disposed || list.SelectedItem is not ListBoxItem { Tag: GitFileChange file }) return;
        try { await owner.ShowDiffAsync(file, isStaged); }
        catch (Exception ex) { if (!disposed) main.Log("Diff: " + ex.Message); }
    }
    internal async Task ChangeStageAsync(bool unstage, bool all = false)
    {
        var list = unstage ? staged : changes;
        var selected = (all ? list.Items : list.SelectedItems).OfType<ListBoxItem>().Select(i => (GitFileChange)i.Tag).ToArray();
        if (selected.Length == 0) return;
        await main.ChangeGitIndexAsync(owner.Project, async () =>
        {
            if (!unstage && !await main.SaveProjectFilesAsync(owner.Project, selected.Select(f => Path.GetFullPath(f.Path, owner.Project.Entry.Path)))) return new CommandResult(1, "Save cancelled.");
            return unstage ? await main.Git.UnstageAsync(owner.Project.Entry.Path, selected, lifetime.Token) : await main.Git.StageAsync(owner.Project.Entry.Path, selected, lifetime.Token);
        });
        await RefreshAsync();
    }
    private async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(message.Text)) { message.Focus(); return; }
        if (await main.ChangeGitIndexAsync(owner.Project, () => main.Git.CommitStagedAsync(owner.Project.Entry.Path, message.Text, main.Log, lifetime.Token))) message.Clear();
        await RefreshAsync();
    }
    private async Task CommitAllAndSyncAsync()
    {
        if (string.IsNullOrWhiteSpace(message.Text)) { message.Focus(); return; }
        if (await main.GitActionAsync(owner.Project, "sync", message.Text)) message.Clear();
        await RefreshAsync();
    }
    public void Dispose() { disposed = true; lifetime.Cancel(); }
}
