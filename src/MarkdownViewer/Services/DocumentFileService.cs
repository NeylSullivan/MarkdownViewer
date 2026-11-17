using System.IO;
using System.Text;
using MarkdownViewer.Models;

namespace MarkdownViewer.Services;

public sealed class DocumentFileService
{
    public const long MaximumFileSize = 20 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<LoadedDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (attempt < 3 && IsTransient(exception))
            {
                await Task.Delay(80 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<LoadedDocument> ReadAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 65_536
        });
        CheckSize(stream.Length);

        using var content = new MemoryStream((int)stream.Length);
        var buffer = new byte[65_536];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            CheckSize(content.Length + count);
            content.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.GetBuffer().AsSpan(0, (int)content.Length);
        var (text, encoding) = Decode(bytes);
        if (text.Contains('\0'))
            throw new InvalidDataException("The file contains binary data and cannot be opened as Markdown.");
        return new LoadedDocument(fullPath, text, encoding, content.Length);
    }

    private static (string Text, string Encoding) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (Encoding.UTF8.GetString(bytes[3..]), "UTF-8 BOM");
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return (Encoding.UTF32.GetString(bytes[4..]), "UTF-32 LE");
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return (new UTF32Encoding(true, false).GetString(bytes[4..]), "UTF-32 BE");
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            return (Encoding.Unicode.GetString(bytes[2..]), "UTF-16 LE");
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            return (Encoding.BigEndianUnicode.GetString(bytes[2..]), "UTF-16 BE");

        try { return (StrictUtf8.GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding(1251).GetString(bytes), "Windows-1251");
        }
    }

    private static void CheckSize(long size)
    {
        if (size > MaximumFileSize)
            throw new InvalidDataException("The file exceeds 20 MiB. Open a smaller document for quick viewing.");
    }

    private static bool IsTransient(IOException exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException || (exception.HResult & 0xFFFF) is 32 or 33;
}
