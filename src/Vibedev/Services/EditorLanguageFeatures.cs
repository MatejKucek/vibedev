using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Vibedev.Core;
using Vibedev.Views;

namespace Vibedev.Services;

internal sealed class EditorLanguageFeatures : IDisposable, IBackgroundRenderer
{
    private readonly EditorTab tab;
    private readonly LanguageServerClient server;
    private readonly Func<BuildLocation, Task> navigate;
    private readonly Action<string> log;
    private readonly ToolTip tooltip = new() { MaxWidth = 650 };
    private CompletionWindow? completion;
    private JsonElement[] diagnostics = [];
    private int version = 1, hoverGeneration;
    private bool disposed, opened;
    private Task sync = Task.CompletedTask;
    private TextEditor Editor => tab.Editor;
    public KnownLayer Layer => KnownLayer.Selection;
    internal IReadOnlyList<JsonElement> Diagnostics => diagnostics;
    internal CompletionWindow? Completion => completion;
    internal Task CompletionApplied { get; private set; } = Task.CompletedTask;

    public EditorLanguageFeatures(EditorTab tab, LanguageServerClient server, Func<BuildLocation, Task> navigate, Action<string> log)
    {
        this.tab = tab; this.server = server; this.navigate = navigate; this.log = log;
        server.Notification += OnNotification;
        Editor.TextChanged += TextChanged;
        Editor.PreviewKeyDown += KeyDown;
        Editor.TextArea.TextEntered += TextEntered;
        Editor.TextArea.TextEntering += TextEntering;
        Editor.MouseHover += Hover;
        Editor.MouseHoverStopped += HoverStopped;
        tab.Saved += Saved;
        Editor.TextArea.TextView.BackgroundRenderers.Add(this);
    }
    public async Task OpenAsync()
    {
        // Queue didOpen before any didChange; UI edits may happen while the write awaits.
        sync = server.OpenAsync(tab.FilePath, Editor.Text, version);
        opened = true;
        await sync;
        if (!disposed) tab.SetLanguageStatus("C# ready · Ctrl+Space completion · F12 definition");
    }
    private async void TextChanged(object? sender, EventArgs e)
    {
        if (!opened || disposed) return;
        version++; hoverGeneration++; tooltip.IsOpen = false;
        diagnostics = []; Editor.TextArea.TextView.InvalidateLayer(Layer);
        var text = Editor.Text; var nextVersion = version;
        sync = ChangeAfterAsync(sync, text, nextVersion);
        try { await sync; } catch (Exception ex) { if (!disposed) log("C# sync: " + ex.Message); }
    }
    private async Task ChangeAfterAsync(Task previous, string text, int nextVersion)
    {
        await previous;
        if (!disposed) await server.ChangeAsync(tab.FilePath, text, nextVersion);
    }
    private async void Saved()
    {
        try { await sync; if (!disposed) await server.SaveAsync(tab.FilePath); }
        catch (Exception ex) { if (!disposed) log("C# save: " + ex.Message); }
    }
    private async void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await ShowCompletionAsync(); }
        else if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await GoToDefinitionAsync(); }
    }
    private async void TextEntered(object? sender, TextCompositionEventArgs e) { if (e.Text == ".") await ShowCompletionAsync(); }
    private void TextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (completion is not null && e.Text.Length > 0 && !char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_') completion.CompletionList.RequestInsertion(e);
    }
    internal async Task<JsonElement> RequestAtCaretAsync(string method)
    {
        await sync;
        return await server.AtAsync(method, tab.FilePath, Editor.TextArea.Caret.Line - 1, Editor.TextArea.Caret.Column - 1);
    }
    internal async Task ShowCompletionAsync()
    {
        var requestedVersion = version; var offset = Editor.CaretOffset;
        try
        {
            var result = await RequestAtCaretAsync("textDocument/completion");
            if (disposed || version != requestedVersion || Editor.CaretOffset != offset || !Editor.IsKeyboardFocusWithin) return;
            var items = result.ValueKind == JsonValueKind.Array ? result : result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var list) ? list : default;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return;
            completion?.Close();
            var window = new CompletionWindow(Editor.TextArea) { CloseWhenCaretAtBeginning = true };
            var start = offset;
            while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1)) || Editor.Document.GetCharAt(start - 1) == '_')) start--;
            window.StartOffset = start;
            var source = new TextDocument(Editor.Text); var sourceVersion = Editor.Document.Version;
            foreach (var item in items.EnumerateArray().OrderBy(i => i.TryGetProperty("sortText", out var sort) ? sort.GetString() : i.GetProperty("label").GetString()))
                if (!item.TryGetProperty("insertTextFormat", out var format) || format.GetInt32() != 2) window.CompletionList.CompletionData.Add(new LspCompletion(item.Clone(), this, source, sourceVersion));
            if (window.CompletionList.CompletionData.Count == 0) return;
            completion = window;
            window.Closed += (_, _) => { if (ReferenceEquals(completion, window)) completion = null; };
            window.Show();
        }
        catch (Exception ex) { if (!disposed) log("C# completion: " + ex.Message); }
    }
    internal async Task GoToDefinitionAsync()
    {
        var requestedVersion = version; var offset = Editor.CaretOffset;
        try
        {
            var result = await RequestAtCaretAsync("textDocument/definition");
            if (disposed || version != requestedVersion || Editor.CaretOffset != offset) return;
            var target = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().FirstOrDefault() : result;
            if (target.ValueKind != JsonValueKind.Object) { tab.SetLanguageStatus("No definition found"); return; }
            var uri = new Uri(target.TryGetProperty("uri", out var direct) ? direct.GetString()! : target.GetProperty("targetUri").GetString()!);
            if (!uri.IsFile) { tab.SetLanguageStatus("This definition is not a local file"); return; }
            var range = target.TryGetProperty("range", out var normal) ? normal : target.GetProperty("targetSelectionRange");
            var start = range.GetProperty("start");
            await navigate(new(uri.LocalPath, start.GetProperty("line").GetInt32() + 1, start.GetProperty("character").GetInt32() + 1));
        }
        catch (Exception ex) { if (!disposed) log("C# definition: " + ex.Message); }
    }
    private async void Hover(object sender, MouseEventArgs e)
    {
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position is null) return;
        var generation = ++hoverGeneration; var requestedVersion = version;
        var offset = Editor.Document.GetOffset(position.Value.Location);
        var diagnostic = diagnostics.FirstOrDefault(d => InRange(d.GetProperty("range"), offset));
        if (diagnostic.ValueKind == JsonValueKind.Object) { ShowTooltip(diagnostic.GetProperty("message").GetString()!); return; }
        try
        {
            await sync;
            var result = await server.AtAsync("textDocument/hover", tab.FilePath, position.Value.Line - 1, position.Value.Column - 1);
            if (disposed || generation != hoverGeneration || requestedVersion != version || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents)) return;
            var text = Documentation(contents);
            if (text.Length > 0) ShowTooltip(text);
        }
        catch (Exception) { /* A hover timeout should not interrupt editing. */ }
    }
    private void ShowTooltip(string text) { tooltip.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }; tooltip.PlacementTarget = Editor; tooltip.IsOpen = true; }
    private void HoverStopped(object sender, MouseEventArgs e) { hoverGeneration++; tooltip.IsOpen = false; }
    private void OnNotification(string method, JsonElement parameters)
    {
        if (method != "textDocument/publishDiagnostics" || parameters.GetProperty("uri").GetString() != LanguageServerClient.FileUri(tab.FilePath)) return;
        Editor.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || parameters.TryGetProperty("version", out var diagnosticVersion) && diagnosticVersion.ValueKind == JsonValueKind.Number && diagnosticVersion.GetInt32() != version) return;
            diagnostics = parameters.GetProperty("diagnostics").EnumerateArray().Select(d => d.Clone()).ToArray();
            var errors = diagnostics.Count(d => !d.TryGetProperty("severity", out var s) || s.GetInt32() == 1);
            var warnings = diagnostics.Count(d => d.TryGetProperty("severity", out var s) && s.GetInt32() == 2);
            tab.SetLanguageStatus($"C# · {errors} errors, {warnings} warnings · Ctrl+Space · F12");
            Editor.TextArea.TextView.InvalidateLayer(Layer);
        });
    }
    private bool InRange(JsonElement range, int offset) => offset >= Offset(Editor.Document, range.GetProperty("start")) && offset <= Offset(Editor.Document, range.GetProperty("end"));
    internal static int Offset(TextDocument document, JsonElement position)
    {
        var line = document.GetLineByNumber(Math.Clamp(position.GetProperty("line").GetInt32() + 1, 1, document.LineCount));
        return line.Offset + Math.Clamp(position.GetProperty("character").GetInt32(), 0, line.Length);
    }
    internal static string Documentation(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n\n", value.EnumerateArray().Select(Documentation)),
        JsonValueKind.Object when value.TryGetProperty("value", out var text) => text.GetString() ?? "",
        _ => ""
    };
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        foreach (var diagnostic in diagnostics)
        {
            var range = diagnostic.GetProperty("range");
            var start = Offset(Editor.Document, range.GetProperty("start")); var end = Offset(Editor.Document, range.GetProperty("end"));
            var segment = new TextSegment { StartOffset = start, Length = Math.Min(Math.Max(1, end - start), Editor.Document.TextLength - start) };
            var warning = diagnostic.TryGetProperty("severity", out var severity) && severity.GetInt32() != 1;
            var pen = new Pen(warning ? Brushes.Goldenrod : Brushes.IndianRed, 1.5) { DashStyle = DashStyles.Dot };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment)) drawingContext.DrawLine(pen, rect.BottomLeft, rect.BottomRight);
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        completion?.Close(); tooltip.IsOpen = false;
        server.Notification -= OnNotification;
        Editor.TextChanged -= TextChanged; Editor.PreviewKeyDown -= KeyDown;
        Editor.TextArea.TextEntered -= TextEntered; Editor.TextArea.TextEntering -= TextEntering;
        Editor.MouseHover -= Hover; Editor.MouseHoverStopped -= HoverStopped;
        tab.Saved -= Saved; Editor.TextArea.TextView.BackgroundRenderers.Remove(this);
        Editor.TextArea.TextView.InvalidateLayer(Layer);
        _ = CloseAsync();
    }
    private async Task CloseAsync() { try { await sync; if (opened) await server.CloseAsync(tab.FilePath); } catch (Exception) { } }

    private sealed class LspCompletion : ICompletionData
    {
        private readonly JsonElement item;
        private readonly EditorLanguageFeatures owner;
        private readonly TextDocument source;
        private readonly ITextSourceVersion sourceVersion;
        public LspCompletion(JsonElement item, EditorLanguageFeatures owner, TextDocument source, ITextSourceVersion sourceVersion)
            => (this.item, this.owner, this.source, this.sourceVersion) = (item, owner, source, sourceVersion);
        public ImageSource? Image => null;
        public string Text => item.GetProperty("label").GetString()!;
        public object Content => Text;
        public object Description => (item.TryGetProperty("detail", out var detail) ? detail.GetString() + "\n" : "") + (item.TryGetProperty("documentation", out var docs) ? Documentation(docs) : "");
        public double Priority => item.TryGetProperty("preselect", out var selected) && selected.GetBoolean() ? 1 : 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => owner.CompletionApplied = CompleteAsync(textArea, completionSegment);
        private async Task CompleteAsync(TextArea textArea, ISegment completionSegment)
        {
            var document = textArea.Document;
            var requestedVersion = owner.version;
            try
            {
                var resolved = item.TryGetProperty("data", out _) ? await owner.server.RequestAsync("completionItem/resolve", item) : item;
                if (owner.disposed || owner.version != requestedVersion) return;
                var replacements = new List<(int Start, int Length, string Text)>();
                void Add(JsonElement edit)
                {
                    var range = edit.TryGetProperty("range", out var normal) ? normal : edit.GetProperty("replace");
                    var start = sourceVersion.MoveOffsetTo(document.Version, Offset(source, range.GetProperty("start")), AnchorMovementType.BeforeInsertion);
                    var end = sourceVersion.MoveOffsetTo(document.Version, Offset(source, range.GetProperty("end")), AnchorMovementType.AfterInsertion);
                    replacements.Add((start, Math.Max(0, end - start), edit.GetProperty("newText").GetString()!));
                }
                if (resolved.TryGetProperty("textEdit", out var primary)) Add(primary);
                else
                {
                    var end = completionSegment.EndOffset;
                    while (end < document.TextLength && (char.IsLetterOrDigit(document.GetCharAt(end)) || document.GetCharAt(end) == '_')) end++;
                    replacements.Add((completionSegment.Offset, end - completionSegment.Offset, resolved.TryGetProperty("insertText", out var text) ? text.GetString()! : Text));
                }
                var main = replacements[0];
                if (resolved.TryGetProperty("additionalTextEdits", out var additional)) foreach (var edit in additional.EnumerateArray()) Add(edit);
                var caret = document.CreateAnchor(main.Start); caret.MovementType = AnchorMovementType.AfterInsertion; caret.SurviveDeletion = true;
                using (document.RunUpdate()) foreach (var change in replacements.OrderByDescending(e => e.Start)) document.Replace(change.Start, change.Length, change.Text);
                textArea.Caret.Offset = caret.Offset;
            }
            catch (Exception ex) { if (!owner.disposed) owner.log("C# completion edit: " + ex.Message); }
        }
    }
}
