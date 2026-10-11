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
    private readonly TextBlock status = Ui.Text("", 11, "#9898A6");
    private readonly TextBox message = new() { ToolTip = "Commit message for the staged files", Margin = new(6), MinHeight = 32 };
    private readonly CancellationTokenSource lifetime = new();
    private bool refreshing, disposed;
    private Task? refreshTask;
    internal IReadOnlyList<GitFileChange> Files { get; private set; } = [];
    internal ListBox ChangesList => changes;
    internal ListBox StagedList => staged;

    public ProjectGitView(MainWindow main, ProjectView owner)
    {
        this.main = main; this.owner = owner;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions[3].Height = GridLength.Auto; staged.MaxHeight = 200;
        ScrollViewer.SetHorizontalScrollBarVisibility(staged, ScrollBarVisibility.Disabled); ScrollViewer.SetHorizontalScrollBarVisibility(changes, ScrollBarVisibility.Disabled);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var tools = new StackPanel { Margin = new(6) };
        tools.Children.Add(status);
        var actions = Ui.Row(Ui.Button("↓ Pull", () => main.GitAction(owner.Project, "pull")), Ui.Button("↑ Push", () => main.GitAction(owner.Project, "push")), Ui.Button("↻", async () => await RefreshAsync()));
        foreach (Button button in actions.Children) button.Padding = new(7, 4, 7, 4);
        ((Button)actions.Children[2]).ToolTip = "Refresh Git changes"; actions.Margin = new(0, 6, 0, 0); tools.Children.Add(actions); root.Children.Add(tools);
        var commit = new StackPanel(); var messageLabel = Ui.Text("Commit message", 10, "#9898A6"); messageLabel.Margin = new(6, 2, 6, 0); commit.Children.Add(messageLabel); commit.Children.Add(message);
        var commitButton = Ui.Button("✓ Commit staged", async () => await CommitAsync(), true); commitButton.Margin = new(6, 0, 6, 8); commitButton.ToolTip = "Commit only staged changes using the message above"; commit.Children.Add(commitButton); Grid.SetRow(commit, 1); root.Children.Add(commit);
        var stagedHeader = Ui.Row(Ui.Text("STAGED", 10, "#9898A6"), Ui.Button("− Selected", async () => await ChangeStageAsync(true)));
        stagedHeader.Margin = new(8, 4, 2, 4); ((Button)stagedHeader.Children[1]).ToolTip = "Unstage selected files; keep their working copies";
        Grid.SetRow(stagedHeader, 2); root.Children.Add(stagedHeader); Grid.SetRow(staged, 3); root.Children.Add(staged);
        var changesHeader = Ui.Row(Ui.Text("CHANGES", 10, "#9898A6"), Ui.Button("+ Selected", async () => await ChangeStageAsync(false)));
        changesHeader.Margin = new(8, 4, 2, 4); ((Button)changesHeader.Children[1]).ToolTip = "Save and stage selected files (Ctrl/Shift+click for multiple files)";
        Grid.SetRow(changesHeader, 4); root.Children.Add(changesHeader); Grid.SetRow(changes, 5); root.Children.Add(changes);
        staged.SelectionChanged += async (_, _) => await ShowSelectionAsync(staged, true);
        changes.SelectionChanged += async (_, _) => await ShowSelectionAsync(changes, false);
        staged.ToolTip = changes.ToolTip = "Click to review diff. Ctrl/Shift+click to select multiple files.";
        Content = root;
    }
    public void RefreshStatus()
    {
        var project = owner.Project;
        status.Text = (project.Git?.IsRepository == true ? $"{project.Git.Branch} · {project.Git.Changes} changes" : "Local project · Git unavailable") + (project.Activity.Length > 0 ? "\n" + project.Activity : "");
        status.Foreground = Ui.Brush(project.Failed ? "#F87171" : "#9898A6");
        status.ToolTip = project.Git?.Error ?? project.Entry.Path;
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
    internal async Task ChangeStageAsync(bool unstage)
    {
        var selected = (unstage ? staged : changes).SelectedItems.OfType<ListBoxItem>().Select(i => (GitFileChange)i.Tag).ToArray();
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
    public void Dispose() { disposed = true; lifetime.Cancel(); }
}
