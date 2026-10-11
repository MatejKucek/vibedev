using System.Text;
using Vibedev.Core;

namespace Vibedev.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vibedev-tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public string File(string relative, string content)
    {
        var path = System.IO.Path.Combine(Path, relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, content); return path;
    }
    public void Dispose()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vibedev-tests")) + System.IO.Path.DirectorySeparatorChar;
        if (!System.IO.Path.GetFullPath(Path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path.");
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}

public sealed class WorkspaceTests
{
    [Fact]
    public void ScanFindsNestedReposAndWorktreesWithoutFollowingGitMetadata()
    {
        using var temp = new TempDirectory();
        temp.File("a/.git/HEAD", "ref: refs/heads/main");
        temp.File("a/child/.git/HEAD", "ref: refs/heads/main");
        temp.File("group/b/.git", "gitdir: elsewhere");
        temp.File("a/.git/internal/.git/HEAD", "ignored");
        temp.File("node_modules/ignored/.git/HEAD", "ignored");
        var result = RepositoryDiscovery.Scan(temp.Path);
        Assert.Equal(3, result.Repositories.Count); Assert.Empty(result.Warnings);
        Assert.Contains(System.IO.Path.Combine(temp.Path, "a", "child"), result.Repositories);
    }
    [Fact]
    public void WorkspaceRoundTripsProjectsPanelsAndFiles()
    {
        using var temp = new TempDirectory(); var store = new WorkspaceStore(temp.Path);
        var state = new WorkspaceState { Projects = [new(temp.Path)], ScanRoot = temp.Path, PanelProjects = new() { ["panel-a"] = temp.Path }, OpenFiles = new() { ["panel-a"] = ["file.cs"] } };
        store.Save(state); var loaded = store.Load();
        Assert.Equal(state.Projects, loaded.Projects); Assert.Equal(temp.Path, loaded.PanelProjects["panel-a"]); Assert.Equal("file.cs", loaded.OpenFiles["panel-a"][0]);
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(temp.Path, "workspace.json.tmp")));
    }
}

public sealed class TextFileTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16")]
    [InlineData("utf32")]
    public async Task SavePreservesEncodingBomAndLineEndings(string kind)
    {
        using var temp = new TempDirectory();
        Encoding encoding = kind switch { "utf8bom" => new UTF8Encoding(true), "utf16" => new UnicodeEncoding(false, true), "utf32" => new UTF32Encoding(false, true), _ => new UTF8Encoding(false) };
        var path = System.IO.Path.Combine(temp.Path, "source.cs");
        await System.IO.File.WriteAllTextAsync(path, "žluťoučký\r\nline\r\n", encoding);
        var file = await TextFile.OpenAsync(path); await file.SaveAsync(file.Text.Replace("line", "changed"));
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes("žluťoučký\r\nchanged\r\n")), await System.IO.File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task ExternalChangesAreProtectedAndExplicitOverwriteWorks()
    {
        using var temp = new TempDirectory(); var path = temp.File("source.cs", "original"); var file = await TextFile.OpenAsync(path);
        await System.IO.File.WriteAllTextAsync(path, "external");
        await Assert.ThrowsAsync<IOException>(() => file.SaveAsync("mine"));
        Assert.Equal("external", await System.IO.File.ReadAllTextAsync(path));
        await file.SaveAsync("mine", true); Assert.Equal("mine", await System.IO.File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task BinaryFilesAreRejected()
    {
        using var temp = new TempDirectory(); var path = temp.File("binary.dat", "a\0b");
        await Assert.ThrowsAsync<IOException>(() => TextFile.OpenAsync(path));
    }
}

public sealed class LaunchTests
{
    [Fact]
    public void CoreClrResolvesJsoncArgumentsEnvironmentAndTaskDependencies()
    {
        using var temp = new TempDirectory();
        temp.File(".vscode/launch.json", """
        { // comments and trailing commas are valid
          "configurations": [{"name":"Run app","type":"coreclr","request":"launch",
          "program":"${workspaceFolder}/bin/app.dll","cwd":"${workspaceFolder}",
          "args":["a b","a'\"&b"],"env":{"CUSTOM":"${workspaceFolderBasename}"},"preLaunchTask":"build",}],
        }
        """);
        temp.File(".vscode/tasks.json", """
        {"tasks":[{"label":"restore","type":"process","command":"dotnet","args":["restore"]},
        {"label":"build","type":"process","command":"dotnet","args":["build","--no-restore"],"dependsOn":["restore"]}]}
        """);
        var config = Assert.Single(LaunchService.Read(temp.Path)); var plan = LaunchService.Resolve(temp.Path, config);
        Assert.Equal("dotnet", plan.Command.FileName); Assert.EndsWith("app.dll", plan.Command.Arguments[0]);
        Assert.Equal("a b", plan.Command.Arguments[1]); Assert.Equal("a'\"&b", plan.Command.Arguments[2]);
        Assert.Equal(System.IO.Path.GetFileName(temp.Path), plan.Command.Environment!["CUSTOM"]);
        Assert.Equal(2, plan.BeforeLaunch.Count); Assert.Equal("restore", plan.BeforeLaunch[0].Arguments[0]); Assert.Equal("build", plan.BeforeLaunch[1].Arguments[0]);
    }
    [Fact]
    public void DotnetConfigurationUsesExplicitProjectAndLaunchProfile()
    {
        using var temp = new TempDirectory();
        temp.File(".vscode/launch.json", """{"configurations":[{"name":"Web","type":"dotnet","request":"launch","projectPath":"${workspaceFolder}/src/web.csproj","launchSettingsProfile":"https","args":["--test"]}]}""");
        var plan = LaunchService.Resolve(temp.Path, LaunchService.Read(temp.Path)[0]);
        Assert.Equal("dotnet", plan.Command.FileName); Assert.Equal("run", plan.Command.Arguments[0]); Assert.Contains("--launch-profile", plan.Command.Arguments); Assert.Equal("--test", plan.Command.Arguments[^1]);
    }
    [Theory]
    [InlineData("node", "launch", "app.js")]
    [InlineData("coreclr", "attach", "app.dll")]
    [InlineData("coreclr", "launch", "${command:pickProcess}")]
    public void UnsupportedConfigurationsFailExplicitly(string type, string request, string program)
    {
        using var temp = new TempDirectory();
        temp.File(".vscode/launch.json", System.Text.Json.JsonSerializer.Serialize(new { configurations = new[] { new { name = "test", type, request, program } } }));
        Assert.Throws<InvalidOperationException>(() => LaunchService.Resolve(temp.Path, LaunchService.Read(temp.Path)[0]));
    }
    [Fact]
    public void MissingLaunchFileOffersNoGuessedCommands()
    {
        using var temp = new TempDirectory(); Assert.Empty(LaunchService.Read(temp.Path));
    }
    [Fact]
    public void CircularBuildTasksAreRejected()
    {
        using var temp = new TempDirectory();
        temp.File(".vscode/launch.json", """{"configurations":[{"name":"test","type":"coreclr","request":"launch","program":"app.dll","preLaunchTask":"a"}]}""");
        temp.File(".vscode/tasks.json", """{"tasks":[{"label":"a","dependsOn":"b"},{"label":"b","dependsOn":"a"}]}""");
        Assert.Contains("Circular", Assert.Throws<InvalidOperationException>(() => LaunchService.Resolve(temp.Path, LaunchService.Read(temp.Path)[0])).Message);
    }
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\a b\\", "\"C:\\a b\\\\\"")]
    public void WindowsArgumentsAreQuotedCorrectly(string input, string expected) => Assert.Equal(expected, WindowsCommandLine.Quote(input));
}
