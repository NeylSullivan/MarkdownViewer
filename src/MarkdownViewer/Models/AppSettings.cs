namespace MarkdownViewer.Models;

public sealed class AppSettings
{
    public bool OutlineVisible { get; set; } = true;
    public double OutlineWidth { get; set; } = 220;
    public string Theme { get; set; } = "System";
    public double FontSize { get; set; } = 16;
    public double ReadingWidth { get; set; } = 860;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 780;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool Maximized { get; set; }
    public List<string> RecentFiles { get; set; } = [];
    public Dictionary<string, double> ScrollPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<OpenDocumentState> OpenDocuments { get; set; } = [];
    public string? ActiveDocumentPath { get; set; }
}
