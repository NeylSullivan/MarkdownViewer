using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MarkdownViewer.Services;

public sealed record SearchMatch(TextPointer Start, TextPointer End);

/// <summary>Searches the document's text, including phrases crossing inline formatting.</summary>
public sealed class DocumentSearchService
{
    private const int MaximumMatches = 10_000;
    private const int MaximumHighlights = 100;
    private readonly List<OriginalBackground> _backgrounds = [];

    public bool Truncated { get; private set; }
    public bool HighlightsLimited { get; private set; }

    public IReadOnlyList<SearchMatch> Find(FlowDocument document, string query, bool darkTheme)
    {
        ArgumentNullException.ThrowIfNull(document);
        Clear();
        if (string.IsNullOrWhiteSpace(query))
            return [];

        query = query.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var (text, segments) = IndexDocument(document);
        var matches = new List<SearchMatch>();
        var comparison = CultureInfo.CurrentCulture.CompareInfo;
        if (comparison.Compare(query, string.Empty, CompareOptions.IgnoreCase) == 0)
            return matches;
        var highlight = new SolidColorBrush(darkTheme
            ? Color.FromRgb(111, 85, 24)
            : Color.FromRgb(255, 226, 143));
        highlight.Freeze();

        var offset = 0;
        while (offset < text.Length)
        {
            var relativeIndex = comparison.IndexOf(text.AsSpan(offset), query.AsSpan(),
                CompareOptions.IgnoreCase, out var length);
            if (relativeIndex < 0)
                break;
            var index = offset + relativeIndex;
            if (matches.Count == MaximumMatches)
            {
                Truncated = true;
                break;
            }

            var endIndex = index + length;
            var first = FindSegment(segments, index);
            var last = FindSegment(segments, Math.Max(index, endIndex - 1));
            if (first >= 0 && last >= first)
            {
                var firstSegment = segments[first];
                var lastSegment = segments[last];
                var start = firstSegment.Pointer.GetPositionAtOffset(
                    Math.Clamp(index - firstSegment.Index, 0, firstSegment.Length), LogicalDirection.Forward);
                var end = lastSegment.Pointer.GetPositionAtOffset(
                    Math.Clamp(endIndex - lastSegment.Index, 0, lastSegment.Length), LogicalDirection.Backward);
                if (start is not null && end is not null && start.CompareTo(end) < 0)
                {
                    matches.Add(new SearchMatch(start, end));
                    if (matches.Count > MaximumHighlights)
                    {
                        HighlightsLimited = true;
                        offset = index + Math.Max(1, length);
                        continue;
                    }
                    for (var i = first; i <= last; i++)
                    {
                        var segment = segments[i];
                        var begin = Math.Max(index, segment.Index) - segment.Index;
                        var finish = Math.Min(endIndex, segment.Index + segment.Length) - segment.Index;
                        if (finish <= begin)
                            continue;

                        var range = new TextRange(
                            segment.Pointer.GetPositionAtOffset(begin, LogicalDirection.Forward)!,
                            segment.Pointer.GetPositionAtOffset(finish, LogicalDirection.Backward)!);
                        var original = range.GetPropertyValue(TextElement.BackgroundProperty);
                        _backgrounds.Add(new OriginalBackground(range, original as Brush));
                    }
                }
            }
            offset = index + Math.Max(1, length);
        }
        // Applying formatting can split Runs and insert document symbols. Resolve
        // every character offset first; the saved TextPointers track later edits.
        foreach (var original in _backgrounds)
            original.Range.ApplyPropertyValue(TextElement.BackgroundProperty, highlight);
        return matches;
    }

    public void Clear()
    {
        foreach (var original in _backgrounds)
            original.Range.ApplyPropertyValue(TextElement.BackgroundProperty, original.Brush);
        _backgrounds.Clear();
        Truncated = false;
        HighlightsLimited = false;
    }

    private static (string Text, List<TextSegment> Segments) IndexDocument(FlowDocument document)
    {
        var text = new StringBuilder();
        var segments = new List<TextSegment>();
        var pointer = document.ContentStart;
        while (pointer is not null && pointer.CompareTo(document.ContentEnd) < 0)
        {
            switch (pointer.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var content = pointer.GetTextInRun(LogicalDirection.Forward);
                    segments.Add(new TextSegment(text.Length, content.Length,
                        pointer.GetPositionAtOffset(0, LogicalDirection.Forward)!));
                    text.Append(content);
                    pointer = pointer.GetPositionAtOffset(content.Length, LogicalDirection.Forward);
                    continue;
                case TextPointerContext.ElementEnd when pointer.Parent is Paragraph:
                    text.Append('\n');
                    break;
                case TextPointerContext.ElementStart when pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    text.Append('\n');
                    break;
                case TextPointerContext.EmbeddedElement:
                    // Do not concatenate text across an image or index embedded button labels.
                    text.Append('\uFFFC');
                    break;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return (text.ToString(), segments);
    }

    private static int FindSegment(List<TextSegment> segments, int index)
    {
        var low = 0;
        var high = segments.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var segment = segments[middle];
            if (index < segment.Index)
                high = middle - 1;
            else if (index >= segment.Index + segment.Length)
                low = middle + 1;
            else
                return middle;
        }
        // Separator positions have no text pointer. A match beginning in a separator
        // starts at the next text run; an ordinary match end always lies in a run.
        return low < segments.Count ? low : high;
    }

    private sealed record TextSegment(int Index, int Length, TextPointer Pointer);
    private sealed record OriginalBackground(TextRange Range, Brush? Brush);
}
