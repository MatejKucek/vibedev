using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Vibedev.Core;

namespace Vibedev.Services;

/// <summary>One Windows ConPTY process, with an output reader that stays alive during console teardown.</summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr console;
    private FileStream? input;
    private FileStream? output;
    private Process? process;
    private Task? outputReader;
    private int disposed;
    public event Action<string>? Output;
    public event Action<int>? Exited;
    public bool IsAlive => disposed == 0 && process is { HasExited: false };

    public void Start(CommandSpec command, int cols = 120, int rows = 24)
    {
        if (console != IntPtr.Zero) throw new InvalidOperationException("The console is already started.");
        Check(CreatePipe(out var readInput, out var writeInput, IntPtr.Zero, 0));
        using (readInput)
        using (writeInput)
        {
            Check(CreatePipe(out var readOutput, out var writeOutput, IntPtr.Zero, 0));
            using (readOutput)
            using (writeOutput)
            {
                Marshal.ThrowExceptionForHR(CreatePseudoConsole(new((short)cols, (short)rows), readInput, writeOutput, 0, out console));
                IntPtr attributes = IntPtr.Zero, environment = IntPtr.Zero;
                try
                {
                    nuint size = 0;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal((nint)size);
                    Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                    Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                    // Explicit null std handles prevent a redirected parent console from being duplicated
                    // into the child instead of connecting it to the pseudoconsole (terminal #15814).
                    var startup = new StartupInfoEx { StartupInfo = new() { cb = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100 }, AttributeList = attributes };
                    var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                        .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);
                    env["TERM"] = "xterm-256color";
                    if (command.Environment is not null)
                        foreach (var (key, value) in command.Environment)
                            if (value is null) env.Remove(key); else env[key] = value;
                    var block = string.Join('\0', env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}")) + "\0";
                    environment = Marshal.StringToHGlobalUni(block);
                    var args = command.RawArguments ?? string.Join(" ", command.Arguments.Select(WindowsCommandLine.Quote));
                    var line = new StringBuilder(WindowsCommandLine.Quote(command.FileName) + " " + args);
                    Check(CreateProcess(null, line, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400,
                        environment, command.WorkingDirectory, ref startup, out var info));
                    CloseHandle(info.Thread);
                    try { process = Process.GetProcessById(info.ProcessId); }
                    finally { CloseHandle(info.Process); }
                    // Transfer ownership of the host ends; pseudoconsole owns its duplicated ends.
                    input = new FileStream(new SafeFileHandle(writeInput.DangerousGetHandle(), true), FileAccess.Write, 4096, false);
                    writeInput.SetHandleAsInvalid();
                    output = new FileStream(new SafeFileHandle(readOutput.DangerousGetHandle(), true), FileAccess.Read, 4096, false);
                    readOutput.SetHandleAsInvalid();
                    outputReader = ReadOutputAsync();
                    _ = ObserveExitAsync();
                }
                catch { Dispose(); throw; }
                finally
                {
                    if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                    if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                }
            }
        }
    }

    public void Write(string text)
    {
        if (!IsAlive) return;
        try { var bytes = Encoding.UTF8.GetBytes(text); input!.Write(bytes); input.Flush(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public void Resize(int cols, int rows)
    {
        if (disposed == 0 && console != IntPtr.Zero) ResizePseudoConsole(console, new((short)Math.Clamp(cols, 2, 500), (short)Math.Clamp(rows, 1, 300)));
    }
    private async Task ReadOutputAsync()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        byte[] bytes = new byte[8192]; char[] chars = new char[8192];
        try
        {
            while (await output!.ReadAsync(bytes) is var count && count > 0)
            {
                var length = decoder.GetChars(bytes, 0, count, chars, 0);
                Output?.Invoke(new string(chars, 0, length));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private async Task ObserveExitAsync()
    {
        var p = process!;
        try
        {
            await p.WaitForExitAsync();
            if (disposed != 0) return;
            var code = p.ExitCode;
            var handle = Interlocked.Exchange(ref console, IntPtr.Zero);
            if (handle != IntPtr.Zero) await Task.Run(() => ClosePseudoConsole(handle));
            if (outputReader is not null) await outputReader;
            if (disposed == 0) Exited?.Invoke(code);
        }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        input?.Dispose();
        var handle = Interlocked.Exchange(ref console, IntPtr.Zero);
        // ClosePseudoConsole waits for its output to drain. Never block the UI/reader thread.
        _ = Task.Run(() => { if (handle != IntPtr.Zero) ClosePseudoConsole(handle); output?.Dispose(); process?.Dispose(); });
    }
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
