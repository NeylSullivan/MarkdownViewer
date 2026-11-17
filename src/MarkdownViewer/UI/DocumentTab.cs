using System.IO;
using MarkdownViewer.Rendering;

namespace MarkdownViewer.UI;

public sealed class DocumentTab(string path, string text, string encodingName)
{
    public string FilePath { get; } = path;
    public string FileName => Path.GetFileName(FilePath);
    public string Text { get; set; } = text;
    public string EncodingName { get; set; } = encodingName;
    public double ScrollOffset { get; set; }
    public RenderedMarkdown? Rendered { get; set; }
    public string? Error { get; set; }
}
