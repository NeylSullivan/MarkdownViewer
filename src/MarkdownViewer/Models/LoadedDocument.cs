namespace MarkdownViewer.Models;

public sealed record LoadedDocument(string FullPath, string Text, string EncodingName, long Size);
