using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Vibedev;
using Vibedev.Core;
using Vibedev.Views;

internal static partial class Program
{
    private static async Task ExecuteMarkdownAsync(MainWindow window, string root)
    {
        window.SetTerminalVisible(false); window.BottomTabs.SelectedIndex = 1;
        var project = Path.Combine(root, "markdown-workspace");
        var docs = Path.Combine(project, "docs"); Directory.CreateDirectory(docs);
        var guidePath = Path.Combine(docs, "guide notes ž.MD");
        await File.WriteAllTextAsync(guidePath, "# Linked guide\n\nOpened inside the same project.");
        var markdownPath = Path.Combine(project, "README.md");
        var source = """
            # A calmer place to build

            Keep your **project notes**, decisions, and next steps beside your code.
            This preview supports *readable Markdown* and `inline code`.

            > One useful step at a time. Your edits stay in the same file when you switch views.

            ## Next steps

            - [x] Set up the workspace
            - [ ] Review the latest changes
            - [ ] Ship the next improvement

            ## Project overview

            | Area | Status | Next action |
            | --- | --- | --- |
            | Editor | Ready | Write your next idea |
            | Markdown | Preview first | Choose Edit to make changes |
            | Terminal | Connected | Run your project |

            ## A small example

            ```csharp
            var message = "Hello, Vibedev!";
            Console.WriteLine(message);
            ```

            Read the [linked guide](docs/guide%20notes%20%C5%BE.MD) or return to [next steps](#next-steps).

            ![Local illustration](docs/pixel.png)

            <script>window.markdownExecuted = true;</script>
            """;
        await File.WriteAllTextAsync(markdownPath, source);
        await File.WriteAllBytesAsync(Path.Combine(docs, "pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aV1sAAAAASUVORK5CYII="));
        window.AddPath(project); window.OpenProject(window.Projects[0]);
        var view = (ProjectView)window.Documents().Single().Content;
        await view.OpenFileAsync(markdownPath);
        var editor = view.Editors.Single(); var preview = editor.Markdown!;
        await WaitUntilAsync(() => preview.RenderedVersion > 0, "Markdown preview initializes and renders on first open");
        Check(editor.IsPreviewVisible && editor.Editor.Visibility == Visibility.Collapsed && editor.Editor.SyntaxHighlighting?.Name == "Markdown",
            "Markdown opens in Preview with dedicated source highlighting ready for Edit");
        using (var document = JsonDocument.Parse(await preview.EvaluateAsync("({heading:document.querySelector('h1').textContent,tasks:document.querySelectorAll('input[type=checkbox]').length,table:!!document.querySelector('.table-wrap table'),code:!!document.querySelector('pre code.language-csharp'),script:window.markdownExecuted===true,image:document.querySelector('img')?.naturalWidth||0})")))
        {
            var data = document.RootElement;
            Check(data.GetProperty("heading").GetString() == "A calmer place to build" && data.GetProperty("tasks").GetInt32() == 3 && data.GetProperty("table").GetBoolean() && data.GetProperty("code").GetBoolean(),
                "Preview renders headings, task checkboxes, tables and fenced code");
            Check(!data.GetProperty("script").GetBoolean() && data.GetProperty("image").GetInt32() > 0,
                "Local images render while raw Markdown HTML remains inert");
        }
        await CaptureAsync(window, "markdown-preview.png");
        await preview.EvaluateAsync("window.scrollTo(0, 200)");
        var toolbar = (Grid)editor.Content;
        Descendants<Button>(toolbar).Single(b => b.Content?.ToString() == "Edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!editor.IsPreviewVisible && editor.Editor.Visibility == Visibility.Visible, "Edit opens the existing source buffer");
        var previous = preview.RenderedVersion;
        editor.Editor.AppendText("\n\n## Live update\n\nAn unsaved edit in the source.");
        Descendants<Button>(toolbar).Single(b => b.Content?.ToString() == "Preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => preview.RenderedVersion > previous, "Switching back to Preview renders the current editor buffer");
        Check(await preview.EvaluateAsync("!!document.getElementById('live-update')") == "true", "New Markdown headings appear after editing");
        Check(await preview.EvaluateAsync("Math.abs(window.scrollY - 200) < 2") == "true", "Switching between Edit and Preview preserves the reading position");
        await editor.SaveAsync();
        previous = preview.RenderedVersion;
        await File.AppendAllTextAsync(markdownPath, "\n\n## External update\n\nWritten outside the editor.");
        await WaitUntilAsync(() => preview.RenderedVersion > previous, "File watcher refreshes the visible Markdown preview");
        Check(await preview.EvaluateAsync("!!document.getElementById('external-update')") == "true" && !editor.IsDirty,
            "External updates reach both the preview and clean source buffer");
        editor.ShowSearch();
        Check(!editor.IsPreviewVisible, "Find switches to source so matches can be edited");
        await view.OpenSearchHitAsync(new(markdownPath, "README.md", 1, 3, 6, "A calmer place"));
        Check(!editor.IsPreviewVisible && editor.Editor.SelectedText == "A calm", "Project search opens Markdown source at the matching selection");
        editor.ShowMarkdownPreview(true);
        await preview.EvaluateAsync("document.querySelector('a[href^=\"docs/guide\"]').click()");
        await WaitUntilAsync(() => view.Editors.Any(e => e.FilePath == guidePath), "Relative Markdown links open Unicode project files inside the editor");
        var guide = view.Editors.Single(e => e.FilePath == guidePath);
        await WaitUntilAsync(() => guide.Markdown?.RenderedVersion > 0, "Uppercase .MD links also open in rendered Preview");
        window.Width = 1000;
        await view.OpenFileAsync(markdownPath); window.UpdateLayout();
        await Task.Delay(200);
        Check(await preview.EvaluateAsync("document.documentElement.scrollWidth <= innerWidth") == "true", "Markdown page fits a narrow project pane without horizontal page overflow");
        var panel = window.Documents().Single(); panel.Float(); await Task.Delay(300);
        Check(panel.IsFloating && preview.IsVisible && await preview.EvaluateAsync("!!document.getElementById('external-update')") == "true",
            "Markdown preview survives moving its project to a floating window");
        panel.Dock(); await Task.Delay(300);
        Check(!panel.IsFloating && preview.IsVisible, "Markdown preview returns when the project is docked again");
        await preview.EvaluateAsync("document.querySelector('a[href=\"#next-steps\"]').click()");
        Check(await preview.EvaluateAsync("Math.abs(document.getElementById('next-steps').getBoundingClientRect().top - 24) < 3") == "true",
            "Heading links scroll within the rendered document");
        File.WriteAllLines(Path.Combine(OutputDirectory, "markdown-results.txt"), Results);
    }
}
