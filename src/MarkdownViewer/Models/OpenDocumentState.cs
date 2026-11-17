namespace MarkdownViewer.Models;

public sealed class OpenDocumentState
{
    public string FilePath { get; set; } = string.Empty;
    public double ScrollOffset { get; set; }
}
