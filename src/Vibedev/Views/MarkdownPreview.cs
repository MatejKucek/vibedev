using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vibedev.Core;

namespace Vibedev.Views;

internal sealed class MarkdownPreview : UserControl, IDisposable
{
    private const string ViewerUrl = "https://preview.vibedev.local/index.html";
    // Composition capture cannot resize its frame pool to zero during docking transitions.
    // https://github.com/MicrosoftEdge/WebView2Feedback/issues/5485
    private readonly WebView2CompositionControl web = new() { MinWidth = 1, MinHeight = 1, DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B) };
    private readonly TextBlock status = Ui.Text("Preparing Markdown preview…", 13, "#9898A6");
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string root, baseUri;
    private readonly Func<string, Task>? openFile;
    private string source = "";
    private int version;
    private bool initializing, disposed, rendering;
    internal bool IsReady { get; private set; }
    internal int RenderedVersion { get; private set; } = -1;

    public MarkdownPreview(string filePath, string projectRoot, Func<string, Task>? openFile)
    {
        this.openFile = openFile;
        root = Path.GetFullPath(projectRoot);
        var relative = Path.GetRelativePath(root, filePath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
        {
            root = Path.GetDirectoryName(filePath)!;
            relative = Path.GetFileName(filePath);
        }
        baseUri = "https://files.vibedev.local/" + string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
        Grid.SetRow(web, 1); body.Children.Add(web);
        status.Margin = new(16, 10, 16, 10); status.VerticalAlignment = VerticalAlignment.Top; body.Children.Add(status); Content = body;
        Loaded += async (_, _) => await InitializeAsync();
        IsVisibleChanged += (_, _) => { debounce.Stop(); if (IsVisible && IsReady) debounce.Start(); };
        debounce.Tick += async (_, _) => { debounce.Stop(); await RenderAsync(); };
    }

    internal void Update(string text)
    {
        if (disposed || text == source && version > 0) return;
        source = text; version++; debounce.Stop();
        if (IsReady && IsVisible) debounce.Start();
    }

    private async Task InitializeAsync()
    {
        if (initializing || disposed) return;
        initializing = true;
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibedev", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
            if (disposed) return;
            await web.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var browser = web.CoreWebView2;
            browser.Settings.AreDefaultContextMenusEnabled = false;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
            browser.Settings.IsStatusBarEnabled = false;
            browser.SetVirtualHostNameToFolderMapping("preview.vibedev.local", Path.Combine(AppContext.BaseDirectory, "Markdown"), CoreWebView2HostResourceAccessKind.DenyCors);
            browser.SetVirtualHostNameToFolderMapping("files.vibedev.local", root, CoreWebView2HostResourceAccessKind.DenyCors);
            browser.NavigationStarting += (_, e) => { if (e.Uri != ViewerUrl) e.Cancel = true; };
            browser.NewWindowRequested += (_, e) => e.Handled = true;
            browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.WebMessageReceived += async (_, e) =>
            {
                if (disposed || e.Source != ViewerUrl) return;
                try
                {
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    var data = message.RootElement;
                    switch (data.GetProperty("type").GetString())
                    {
                        case "ready": IsReady = true; await RenderAsync(); break;
                        case "rendered": RenderedVersion = data.GetProperty("version").GetInt32(); break;
                        case "link": await OpenLinkAsync(data.GetProperty("url").GetString()!); break;
                        case "copy":
                            var text = data.GetProperty("text").GetString();
                            if (!string.IsNullOrEmpty(text)) { Clipboard.SetText(text); Send(new { type = "copied" }); }
                            break;
                    }
                }
                catch (Exception ex) { ShowError("Markdown action failed: " + ex.Message); }
            };
            browser.NavigationCompleted += (_, e) => { if (!e.IsSuccess) ShowError("Preview could not load. Choose Edit to view the source."); };
            web.Source = new Uri(ViewerUrl);
        }
        catch (Exception ex) { if (!disposed) ShowError("Preview unavailable. Choose Edit to view the source.\n" + ex.Message); }
    }

    private async Task RenderAsync()
    {
        if (disposed || !IsReady || !IsVisible || rendering) return;
        rendering = true;
        var requested = version;
        var text = source;
        try
        {
            var html = await Task.Run(() => MarkdownPreviewRenderer.Render(text));
            if (disposed || requested != version) return;
            Send(new { type = "render", html, baseUri, version = requested });
            status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { if (!disposed) ShowError("Preview could not render. Choose Edit to view the source.\n" + ex.Message); }
        finally
        {
            rendering = false;
            if (!disposed && IsVisible && requested != version) debounce.Start();
        }
    }

    private async Task OpenLinkAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme == "https" && uri.Host == "files.vibedev.local")
        {
            var path = Path.GetFullPath(Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar), root);
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                ShowError("Linked file was not found in this project."); return;
            }
            if (openFile is not null) await openFile(path);
        }
        else if (uri.Scheme is "https" or "http" or "mailto")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowError(string message) { status.Text = message; status.Visibility = Visibility.Visible; }
    private void Send(object message) { if (!disposed) web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
    internal Task<string> EvaluateAsync(string script) => web.CoreWebView2.ExecuteScriptAsync(script);
    internal Task CaptureAsync(Stream stream) => web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    public void Dispose() { if (disposed) return; disposed = true; debounce.Stop(); web.Dispose(); }
}
