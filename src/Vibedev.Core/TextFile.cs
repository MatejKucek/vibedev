using System.Text;

namespace Vibedev.Core;

public sealed class TextFile
{
    // Panels can own different snapshots of the same path. Keep the conflict check and
    // replacement in one process-wide critical section, with a bounded set of locks.
    private static readonly SemaphoreSlim[] SaveLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public string Path { get; }
    public string Text { get; private set; }
    public Encoding Encoding { get; }
    private byte[] original;

    private TextFile(string path, string text, Encoding encoding, byte[] bytes)
        => (Path, Text, Encoding, original) = (path, text, encoding, bytes);

    public static async Task<TextFile> OpenAsync(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("This file is larger than the editor's 8 MB limit.");
        var bytes = await File.ReadAllBytesAsync(path);
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) { encoding = new UTF32Encoding(false, true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(true, true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        string text;
        try { text = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new IOException("This file is binary or uses an unsupported text encoding."); }
        if (text.Contains('\0')) throw new IOException("Binary files cannot be opened in the text editor.");
        return new(System.IO.Path.GetFullPath(path), text, encoding, bytes);
    }

    public async Task SaveAsync(string text, bool overwriteExternalChanges = false)
    {
        var gate = SaveLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path) % (uint)SaveLocks.Length];
        await gate.WaitAsync();
        try
        {
            await CheckForExternalChangesAsync(overwriteExternalChanges);
            var bytes = Encoding.GetPreamble().Concat(Encoding.GetBytes(text)).ToArray();
            // A sibling temporary file keeps the old file intact until replacement succeeds.
            var temp = Path + ".vibedev-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, bytes);
                await CheckForExternalChangesAsync(overwriteExternalChanges);
                if (File.Exists(Path)) File.Replace(temp, Path, null);
                else File.Move(temp, Path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            original = bytes;
            Text = text;
        }
        finally { gate.Release(); }
    }

    private async Task CheckForExternalChangesAsync(bool overwrite)
    {
        if (!overwrite && (!File.Exists(Path) || !(await File.ReadAllBytesAsync(Path)).AsSpan().SequenceEqual(original)))
            throw new IOException("The file changed on disk. Reopen it or explicitly choose to overwrite the disk version.");
    }
}
