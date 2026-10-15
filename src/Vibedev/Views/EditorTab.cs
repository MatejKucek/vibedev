using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Indentation.CSharp;
using Vibedev.Core;
using Vibedev.Services;

namespace Vibedev.Views;

public sealed class EditorTab : TabItem, IDisposable
{
    private TextFile file;
    private readonly TextEditor editor;
    private readonly TextBlock title;
    private readonly TextBox find = new() { Width = 150, ToolTip = "Find text", Tag = "Find…" };
    private readonly TextBox replace = new() { Width = 150, ToolTip = "Replacement", Tag = "Replace with…" };
    private readonly Border search;
    private readonly TextBlock searchStatus = Ui.Text("", 11, "#9898A6");
    private readonly TextBlock location = Ui.Text("", 10, "#777784");
    private readonly Action changed;
    private string languageStatus = "";
    private string saveStatus = "Autosave on";
    private readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private bool saving, disposed, autosavePaused;
    private readonly MarkdownPreview? markdown;
    private readonly Button? previewButton, editButton;
    public event Action? Saved;
    private static readonly HashSet<string> DarkDefinitions = [];
    public string FilePath => file.Path;
    public bool IsDirty => editor.Text != file.Text;
    public TextEditor Editor => editor;
    internal MarkdownPreview? Markdown => markdown;
    internal bool IsPreviewVisible => markdown?.Visibility == Visibility.Visible;

    public EditorTab(TextFile file, Action<EditorTab> close, Action changed, string? projectRoot = null, Func<string, Task>? openFile = null)
    {
        Style = (Style)Application.Current.FindResource(typeof(TabItem));
        this.file = file; this.changed = changed;
        title = Ui.Text(Path.GetFileName(file.Path), 12); title.TextWrapping = TextWrapping.NoWrap;
        var x = Ui.Button("×", () => close(this)); x.Padding = new(5, 0, 5, 0); x.Margin = new(10, 0, 0, 0); x.BorderThickness = new(0); x.Background = Brushes.Transparent;
        Header = Ui.Row(title, x); ToolTip = file.Path;
        x.ToolTip = "Close file (Ctrl+W)";
        editor = new TextEditor
        {
            Text = file.Text, Background = Ui.Brush("#18181B"), Foreground = Ui.Brush("#D4D4DC"),
            FontFamily = new("Cascadia Code, Consolas"), FontSize = 13, ShowLineNumbers = true,
            LineNumbersForeground = Ui.Brush("#656573"), Padding = new(8, 10, 8, 10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SyntaxHighlighting = Highlight(file.Path)
        };
        editor.Options.ConvertTabsToSpaces = true; editor.Options.IndentationSize = 4; editor.Options.EnableEmailHyperlinks = false; editor.Options.EnableHyperlinks = false;
        editor.TextArea.SelectionBrush = Ui.Brush("#55416F"); editor.TextArea.Caret.CaretBrush = Ui.Brush("#A78BFA");
        if (Path.GetExtension(file.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase)) editor.TextArea.IndentationStrategy = new CSharpIndentationStrategy(editor.Options);
        autosave.Tick += async (_, _) => { autosave.Stop(); await SaveAsync(true); };
        editor.TextChanged += (_, _) =>
        {
            title.Text = Path.GetFileName(file.Path) + (IsDirty ? " ●" : ""); changed();
            autosave.Stop(); if (!disposed && !autosavePaused && IsDirty) autosave.Start();
            markdown?.Update(editor.Text);
        };
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateLocation();
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            switch (e.Key)
            {
                case Key.S: e.Handled = true; await SaveAsync(); break;
                case Key.F: case Key.H: e.Handled = true; ShowSearch(); break;
                case Key.W: e.Handled = true; close(this); break;
            }
        };
        var editing = new Grid(); editing.Children.Add(editor);
        if (MarkdownPreviewRenderer.Supports(file.Path))
        {
            editor.WordWrap = true;
            markdown = new MarkdownPreview(file.Path, projectRoot ?? Path.GetDirectoryName(file.Path)!, openFile);
            editing.Children.Add(markdown);
            var modes = new DockPanel { Margin = new(10, 6, 6, 6) };
            previewButton = Ui.Button("Preview", () => ShowMarkdownPreview(true));
            editButton = Ui.Button("Edit", () => ShowMarkdownPreview(false));
            previewButton.Padding = editButton.Padding = new(10, 4, 10, 4);
            previewButton.ToolTip = "Read the rendered Markdown, including unsaved edits";
            editButton.ToolTip = "Edit Markdown source";
            var buttons = Ui.Row(previewButton, editButton); DockPanel.SetDock(buttons, Dock.Right); modes.Children.Add(buttons);
            var label = Ui.Text("MARKDOWN", 10, "#9898A6"); label.VerticalAlignment = VerticalAlignment.Center; modes.Children.Add(label);
            layout.Children.Add(modes);
            ShowMarkdownPreview(true);
        }
        var tools = new WrapPanel { Margin = new(6) };
        tools.Children.Add(find); tools.Children.Add(Ui.Button("Next", FindNext)); tools.Children.Add(replace);
        tools.Children.Add(Ui.Button("Replace", ReplaceOne)); tools.Children.Add(Ui.Button("All", ReplaceAll)); tools.Children.Add(Ui.Button("×", () => { search!.Visibility = Visibility.Collapsed; editor.Focus(); })); tools.Children.Add(searchStatus);
        search = new Border { Child = tools, Background = Ui.Brush("#24242A"), Visibility = Visibility.Collapsed };
        find.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } if (e.Key == Key.Escape) search.Visibility = Visibility.Collapsed; };
        Grid.SetRow(search, 1); layout.Children.Add(search); Grid.SetRow(editing, 2); layout.Children.Add(editing);
        var footer = new Border { Background = Ui.Brush("#202024"), Padding = new(12, 5, 12, 5), Child = location }; Grid.SetRow(footer, 3); layout.Children.Add(footer);
        Content = layout; UpdateLocation();
    }
    private void UpdateLocation() => location.Text = $"Ln {editor.TextArea.Caret.Line}, Col {editor.TextArea.Caret.Column}     {file.Encoding.WebName.ToUpperInvariant()}     {Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant()}     {saveStatus}     {languageStatus}";
    internal void SetLanguageStatus(string text) { languageStatus = text; UpdateLocation(); }
    public void ShowSearch() { ShowMarkdownPreview(false); search.Visibility = Visibility.Visible; if (editor.SelectedText.Length > 0) find.Text = editor.SelectedText; find.Focus(); find.SelectAll(); }
    internal void ShowMarkdownPreview(bool preview)
    {
        if (markdown is null) return;
        markdown.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        editor.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        previewButton!.Background = Ui.Brush(preview ? "#343044" : "#2A2A30");
        previewButton.BorderBrush = Ui.Brush(preview ? "#A78BFA" : "#3C3C44");
        editButton!.Background = Ui.Brush(preview ? "#2A2A30" : "#343044");
        editButton.BorderBrush = Ui.Brush(preview ? "#3C3C44" : "#A78BFA");
        if (preview) { if (search is not null) search.Visibility = Visibility.Collapsed; markdown.Update(editor.Text); }
        else editor.Focus();
    }
    private void FindNext()
    {
        if (find.Text.Length == 0) return;
        var start = Math.Min(editor.SelectionStart + editor.SelectionLength, editor.Text.Length);
        var index = editor.Text.IndexOf(find.Text, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = editor.Text.IndexOf(find.Text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { searchStatus.Text = "No matches"; return; }
        editor.Select(index, find.Text.Length); editor.ScrollToLine(editor.Document.GetLineByOffset(index).LineNumber); searchStatus.Text = "";
    }
    private void ReplaceOne()
    {
        if (find.Text.Length == 0) return;
        if (!editor.SelectedText.Equals(find.Text, StringComparison.OrdinalIgnoreCase)) { FindNext(); return; }
        editor.Document.Replace(editor.SelectionStart, editor.SelectionLength, replace.Text); FindNext();
    }
    private void ReplaceAll()
    {
        if (find.Text.Length == 0) return;
        var text = editor.Text.Replace(find.Text, replace.Text, StringComparison.OrdinalIgnoreCase);
        editor.Document.Replace(0, editor.Document.TextLength, text); searchStatus.Text = "Replaced all";
    }
    public Task<bool> SaveAsync() => SaveAsync(false);
    private async Task<bool> SaveAsync(bool automatic)
    {
        autosave.Stop();
        await saveLock.WaitAsync();
        try
        {
            if (disposed) return !IsDirty;
            if (automatic && autosavePaused) return !IsDirty;
            if (!IsDirty) { if (!automatic) ResumeAutosave("Autosave on"); return true; }
            var snapshot = editor.Text;
            saving = true;
            try
            {
                try { await file.SaveAsync(snapshot); }
                catch (IOException ex) when (!automatic && ex.Message.StartsWith("The file changed on disk", StringComparison.Ordinal))
                {
                    PauseAutosave(ex);
                    if (MessageBox.Show(Window.GetWindow(this), ex.Message + "\n\nOverwrite the disk version?", "File changed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                    await file.SaveAsync(snapshot, true);
                }
            }
            catch (Exception ex)
            {
                PauseAutosave(ex);
                if (!automatic) Ui.Error(ex.Message, Window.GetWindow(this));
                return false;
            }
            finally { saving = false; }
            ResumeAutosave(automatic ? "Autosaved" : "Saved");
            title.Text = Path.GetFileName(file.Path) + (IsDirty ? " ●" : ""); changed(); Saved?.Invoke();
            if (!disposed && IsDirty) { autosave.Stop(); autosave.Start(); }
            return true;
        }
        finally { saveLock.Release(); }
    }
    public async Task<bool> CanCloseAsync()
    {
        autosave.Stop();
        await saveLock.WaitAsync(); saveLock.Release();
        if (!IsDirty) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), $"Save changes to {Path.GetFileName(file.Path)}?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel && !disposed && !autosavePaused) autosave.Start();
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SaveAsync());
    }
    public void Dispose() { if (disposed) return; disposed = true; autosave.Stop(); markdown?.Dispose(); }
    private void PauseAutosave(Exception error)
    {
        autosavePaused = true; autosave.Stop(); saveStatus = "Autosave paused · Ctrl+S to resolve";
        location.ToolTip = error.Message; location.Foreground = Ui.Brush("#FBBF24"); UpdateLocation();
    }
    private void ResumeAutosave(string status)
    {
        autosavePaused = false; saveStatus = status; location.ToolTip = null;
        location.Foreground = Ui.Brush("#9898A6"); UpdateLocation();
    }
    public async Task RefreshFromDiskAsync()
    {
        if (disposed || saving || IsDirty || !File.Exists(FilePath)) return;
        var originalText = editor.Text;
        var updated = await TextFile.OpenAsync(FilePath);
        if (disposed || saving || IsDirty || editor.Text != originalText) return;
        file = updated;
        ResumeAutosave("Autosave on");
        if (editor.Text != updated.Text)
        {
            var offset = editor.CaretOffset;
            editor.Text = updated.Text;
            editor.CaretOffset = Math.Min(offset, editor.Text.Length);
            editor.Document.UndoStack.ClearAll();
            title.Text = Path.GetFileName(FilePath); changed();
        }
    }
    private static IHighlightingDefinition? Highlight(string path)
    {
        if (MarkdownPreviewRenderer.Supports(path)) return MarkdownHighlighting.Definition;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension)
            ?? HighlightingManager.Instance.GetDefinition(extension is ".json" or ".jsonc" ? "JavaScript" : extension is ".csproj" or ".props" or ".targets" or ".xaml" ? "XML" : "C#");
        if (definition is null || !DarkDefinitions.Add(definition.Name)) return definition;
        foreach (var color in definition.NamedHighlightingColors)
        {
            var name = color.Name.ToLowerInvariant();
            color.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(
                name.Contains("comment") ? "#6A9955" : name.Contains("string") || name.Contains("char") ? "#CE9178" : name.Contains("number") || name.Contains("digit") ? "#B5CEA8" : name.Contains("keyword") || name.Contains("modifier") ? "#C586C0" : name.Contains("type") || name.Contains("class") ? "#4EC9B0" : name.Contains("tag") ? "#569CD6" : name.Contains("attribute") ? "#9CDCFE" : "#DCDCAA"));
        }
        return definition;
    }
}
