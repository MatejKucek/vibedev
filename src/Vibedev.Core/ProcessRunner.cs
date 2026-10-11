using System.Diagnostics;
using System.Text;

namespace Vibedev.Core;

public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null, string? RawArguments = null, Encoding? OutputEncoding = null);

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(CommandSpec command, Action<string>? output = null,
        CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (command.OutputEncoding is not null) { start.StandardOutputEncoding = command.OutputEncoding; start.StandardErrorEncoding = command.OutputEncoding; }
        if (command.RawArguments is not null) start.Arguments = command.RawArguments;
        else foreach (var arg in command.Arguments) start.ArgumentList.Add(arg);
        if (command.Environment is not null)
            foreach (var (key, value) in command.Environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        });
        var lines = new List<string>();
        var sync = new object();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (sync) lines.Add(line);
                output?.Invoke(line);
            }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync());
        cancellation.ThrowIfCancellationRequested();
        return new(process.ExitCode, string.Join(System.Environment.NewLine, lines));
    }
}
