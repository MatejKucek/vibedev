using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vibedev.Core;

public sealed record LaunchConfiguration(string Name, JsonElement Settings)
{
    public override string ToString() => Name;
}
public sealed record LaunchPlan(CommandSpec Command, IReadOnlyList<CommandSpec> BeforeLaunch);

public static partial class LaunchService
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static IReadOnlyList<LaunchConfiguration> Read(string root)
    {
        var path = Path.Combine(root, ".vscode", "launch.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
        if (!doc.RootElement.TryGetProperty("configurations", out var configs)) return [];
        return configs.EnumerateArray().Select((c, i) => new LaunchConfiguration(Get(c, "name") ?? $"Configuration {i + 1}", c.Clone())).ToArray();
    }

    public static LaunchPlan Resolve(string root, LaunchConfiguration configuration)
    {
        var c = configuration.Settings;
        var type = Get(c, "type");
        if (Get(c, "request") != "launch") throw new InvalidOperationException("Only launch configurations can be run. Attach needs a debugger.");
        if (type is not ("coreclr" or "clr" or "dotnet")) throw new InvalidOperationException($"Launch type '{type}' is not supported. Use coreclr, clr or dotnet.");
        foreach (var unsupported in new[] { "pipeTransport", "envFile", "postDebugTask", "windows" })
            if (c.TryGetProperty(unsupported, out _)) throw new InvalidOperationException($"'{unsupported}' is not supported by this runner.");
        if (type != "dotnet" && (c.TryGetProperty("launchSettingsProfile", out _) || c.TryGetProperty("launchSettingsFilePath", out _)))
            throw new InvalidOperationException("Use a dotnet launch configuration with projectPath to run a launchSettingsProfile.");
        var cwd = ResolvePath(root, Expand(Get(c, "cwd") ?? root, root));
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Launch working directory does not exist: {cwd}");
        var env = new Dictionary<string, string?>();
        if (c.TryGetProperty("env", out var values))
            foreach (var p in values.EnumerateObject()) env[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : Expand(p.Value.GetString()!, root);
        var args = new List<string>();
        string executable;
        string? rawArguments = null;
        if (type == "dotnet")
        {
            var project = Get(c, "projectPath");
            if (project is null)
            {
                var projects = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
                if (projects.Length != 1) throw new InvalidOperationException("Set projectPath in this dotnet launch configuration so the app knows which .csproj to run.");
                project = projects[0];
            }
            project = ResolvePath(root, Expand(project, root));
            executable = "dotnet";
            args.AddRange(["run", "--project", project]);
            if (Get(c, "launchSettingsProfile") is { } profile) args.AddRange(["--launch-profile", Expand(profile, root)]);
            args.Add("--");
        }
        else
        {
            var program = Get(c, "program") ?? throw new InvalidOperationException("The launch configuration needs a program path.");
            program = ResolvePath(root, Expand(program, root));
            executable = program;
            if (Path.GetExtension(program).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                executable = "dotnet";
                args.Add(program);
            }
        }
        if (c.TryGetProperty("args", out var arguments))
        {
            if (arguments.ValueKind == JsonValueKind.Array) args.AddRange(arguments.EnumerateArray().Select(a => Expand(a.GetString()!, root)));
            else if (arguments.ValueKind == JsonValueKind.String)
                rawArguments = string.Join(" ", args.Select(WindowsCommandLine.Quote)) + " " + Expand(arguments.GetString()!, root);
            else throw new InvalidOperationException("args must be an array of strings or a command-line string.");
        }
        List<CommandSpec> tasks = [];
        if (Get(c, "preLaunchTask") is { } task) ResolveTask(root, task, tasks, new HashSet<string>(), new HashSet<string>());
        return new(new(executable, args, cwd, env, rawArguments), tasks);
    }

    private static void ResolveTask(string root, string label, List<CommandSpec> commands, HashSet<string> visiting, HashSet<string> done)
    {
        if (done.Contains(label)) return;
        if (!visiting.Add(label)) throw new InvalidOperationException($"Circular task dependency: {label}");
        var file = Path.Combine(root, ".vscode", "tasks.json");
        if (!File.Exists(file)) throw new InvalidOperationException($"preLaunchTask '{label}' needs .vscode/tasks.json.");
        using var doc = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
        var task = doc.RootElement.GetProperty("tasks").EnumerateArray().FirstOrDefault(t => Get(t, "label") == label);
        if (task.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Task '{label}' was not found in tasks.json.");
        if (task.TryGetProperty("windows", out _)) throw new InvalidOperationException($"Task '{label}' has a Windows override. Move its settings into the task itself.");
        if (task.TryGetProperty("isBackground", out var bg) && bg.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"Background task '{label}' cannot be used as a preLaunchTask.");
        if (task.TryGetProperty("dependsOn", out var depends))
        {
            var labels = depends.ValueKind == JsonValueKind.String ? [depends.GetString()!] : depends.EnumerateArray().Select(x => x.GetString()!).ToArray();
            foreach (var dep in labels) ResolveTask(root, dep, commands, visiting, done);
        }
        if (Get(task, "command") is { } command)
        {
            var type = Get(task, "type") ?? "process";
            if (type is not ("process" or "shell")) throw new InvalidOperationException($"Task type '{type}' needs a VS Code extension. Use a process or shell task.");
            var args = task.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String
                ? Expand(x.GetString()!, root) : throw new InvalidOperationException("Task arguments must be strings.")).ToArray() : [];
            var cwd = root;
            var env = new Dictionary<string, string?>();
            if (task.TryGetProperty("options", out var options))
            {
                cwd = ResolvePath(root, Expand(Get(options, "cwd") ?? root, root));
                if (options.TryGetProperty("env", out var taskEnv))
                    foreach (var p in taskEnv.EnumerateObject()) env[p.Name] = Expand(p.Value.GetString()!, root);
                if (options.TryGetProperty("shell", out _)) throw new InvalidOperationException("Custom task shells are not supported. Shell tasks use Windows PowerShell.");
            }
            command = Expand(command, root);
            if (type == "shell")
            {
                var script = (File.Exists(command) ? "& " + PowerShellQuote(command) : command)
                    + (args.Length == 0 ? "" : " " + string.Join(" ", args.Select(PowerShellQuote)));
                commands.Add(new("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], cwd, env));
            }
            else commands.Add(new(command, args, cwd, env));
        }
        else if (!task.TryGetProperty("dependsOn", out _)) throw new InvalidOperationException($"Task '{label}' has no command.");
        visiting.Remove(label);
        done.Add(label);
    }

    public static string Expand(string value, string root)
        => VariableRegex().Replace(value, m => m.Groups[1].Value switch
        {
            "workspaceFolder" => root,
            "workspaceFolderBasename" => Path.GetFileName(root.TrimEnd('\\', '/')),
            "pathSeparator" or "/" => Path.DirectorySeparatorChar.ToString(),
            var key when key.StartsWith("env:", StringComparison.Ordinal) => Environment.GetEnvironmentVariable(key[4..]) ?? "",
            var key => throw new InvalidOperationException($"VS Code variable '${{{key}}}' needs an extension or interactive input and cannot be resolved here.")
        });
    private static string ResolvePath(string root, string value) => Path.GetFullPath(value, root);
    private static string? Get(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";
    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex VariableRegex();
}

public static class WindowsCommandLine
{
    // CommandLineToArgvW-compatible quoting, including backslashes before a closing quote.
    public static string Quote(string value)
    {
        if (value.Length != 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}
