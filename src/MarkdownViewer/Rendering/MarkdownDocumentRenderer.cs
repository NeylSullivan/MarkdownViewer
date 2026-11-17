using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace MarkdownViewer.Rendering;

public sealed record DocumentHeading(string Title, int Level, string Anchor, WpfBlock Block);
public sealed record RenderedMarkdown(FlowDocument Document, IReadOnlyList<DocumentHeading> Headings);

public sealed class MarkdownDocumentRenderer
{
    private static readonly HttpClient ImageClient = new();

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    public RenderedMarkdown Render(string markdown, string filePath, bool darkTheme, double fontSize, Action<string> navigate)
    {
        var context = new RenderContext(filePath, darkTheme, fontSize, navigate);
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            Foreground = context.Text,
            Background = context.Background,
            PagePadding = new Thickness(32, 24, 32, 40),
            ColumnWidth = double.PositiveInfinity,
            LineHeight = fontSize * 1.6,
            IsHyphenationEnabled = false
        };
        var parsed = Markdown.Parse(markdown, Pipeline);
        context.Document = document;
        foreach (var block in parsed)
            document.Blocks.Add(context.RenderBlock(block));
        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph());
        return new RenderedMarkdown(document, context.Headings);
    }

    private sealed class RenderContext
    {
        private readonly string _directory;
        private readonly bool _dark;
        private readonly double _fontSize;
        private readonly Action<string> _navigate;
        private readonly Brush _muted;
        private readonly Brush _border;
        private readonly Brush _surface;
        private readonly Brush _accent;
        private readonly Brush _codeBackground;

        public Brush Text { get; }
        public Brush Background { get; }
        public FlowDocument Document { get; set; } = null!;
        public List<DocumentHeading> Headings { get; } = [];

        public RenderContext(string filePath, bool dark, double fontSize, Action<string> navigate)
        {
            _directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
            _dark = dark;
            _fontSize = fontSize;
            _navigate = navigate;
            Text = Brush(dark ? "#E7E7E9" : "#25272B");
            Background = Brush(dark ? "#202123" : "#FFFFFF");
            _muted = Brush(dark ? "#A4A8B1" : "#666B74");
            _border = Brush(dark ? "#34363B" : "#E3E5E8");
            _surface = Brush(dark ? "#292B2F" : "#F3F4F6");
            _accent = Brush(dark ? "#8BBCFF" : "#2765B3");
            _codeBackground = Brush(dark ? "#191A1D" : "#F6F7F9");
        }

        public WpfBlock RenderBlock(MdBlock block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var paragraph = Paragraph(heading.Inline);
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.FontSize = _fontSize * (heading.Level switch { 1 => 1.9, 2 => 1.45, 3 => 1.2, _ => 1.05 });
                    paragraph.LineHeight = paragraph.FontSize * 1.35;
                    paragraph.Margin = new Thickness(0, heading.Level == 1 ? 8 : 22, 0, 10);
                    paragraph.KeepWithNext = true;
                    var title = InlineText(heading.Inline);
                    Headings.Add(new DocumentHeading(title, heading.Level, heading.GetAttributes().Id ?? string.Empty, paragraph));
                    return paragraph;
                }
                case ParagraphBlock paragraph:
                    return Paragraph(paragraph.Inline);
                case CodeBlock code:
                    return RenderCode(code.Lines.ToString(), (code as FencedCodeBlock)?.Info ?? string.Empty);
                case QuoteBlock quote:
                {
                    var section = RenderContainer(quote);
                    section.BorderBrush = _accent;
                    section.BorderThickness = new Thickness(3, 0, 0, 0);
                    section.Padding = new Thickness(14, 8, 12, 2);
                    section.Margin = new Thickness(0, 12, 0, 14);
                    section.Foreground = _muted;
                    section.Background = _surface;
                    return section;
                }
                case ListBlock list:
                {
                    var result = new WpfList
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 2, 0, 12),
                        Padding = new Thickness(24, 0, 0, 0)
                    };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start))
                        result.StartIndex = Math.Max(1, start);
                    foreach (var child in list.OfType<ListItemBlock>())
                    {
                        var item = new ListItem { Margin = new Thickness(0, 2, 0, 2) };
                        foreach (var content in child)
                        {
                            var rendered = RenderBlock(content);
                            if (rendered is Paragraph) rendered.Margin = new Thickness(0, 0, 0, list.IsLoose ? 8 : 2);
                            item.Blocks.Add(rendered);
                        }
                        result.ListItems.Add(item);
                    }
                    return result;
                }
                case Markdig.Extensions.Tables.Table table:
                    return RenderTable(table);
                case ThematicBreakBlock:
                    return new Paragraph
                    {
                        FontSize = 1, LineHeight = 1,
                        BorderBrush = _border, BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 15, 0, 18)
                    };
                case HtmlBlock html:
                    return RenderCode(html.Lines.ToString(), "html");
                case ContainerBlock container:
                    return RenderContainer(container);
                case LeafBlock leaf:
                    return leaf.Inline is not null ? Paragraph(leaf.Inline) : new Paragraph(new Run(leaf.Lines.ToString()));
                default:
                    return new Paragraph();
            }
        }

        private Section RenderContainer(ContainerBlock container)
        {
            var section = new Section();
            foreach (var block in container)
                section.Blocks.Add(RenderBlock(block));
            return section;
        }

        private Paragraph Paragraph(ContainerInline? inline)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            if (inline is not null) AddInlines(paragraph.Inlines, inline);
            return paragraph;
        }

        private void AddInlines(InlineCollection target, ContainerInline source)
        {
            foreach (var child in source)
                target.Add(RenderInline(child));
        }

        private WpfInline RenderInline(MdInline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    return new Run(literal.Content.ToString());
                case CodeInline code:
                    return new Run(code.Content) { FontFamily = new FontFamily("Consolas"), FontSize = _fontSize * .92, Background = _codeBackground };
                case LineBreakInline lineBreak:
                    return lineBreak.IsHard ? new LineBreak() : new Run(" ");
                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount == 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis);
                    return span;
                }
                case LinkInline link when link.IsImage:
                    return RenderImage(link);
                case LinkInline link:
                {
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
                    var hyperlink = CreateLink(url, link.Title);
                    AddInlines(hyperlink.Inlines, link);
                    return hyperlink;
                }
                case AutolinkInline autolink:
                {
                    var hyperlink = CreateLink(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url, null);
                    hyperlink.Inlines.Add(new Run(autolink.Url));
                    return hyperlink;
                }
                case HtmlEntityInline entity:
                    return new Run(entity.Transcoded.ToString());
                case HtmlInline html:
                    return new Run(html.Tag) { Foreground = _muted };
                case TaskList task:
                    return new Run(task.Checked ? "☑ " : "☐ ") { FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = task.Checked ? _accent : _muted };
                case ContainerInline container:
                {
                    var span = new Span();
                    AddInlines(span.Inlines, container);
                    return span;
                }
                default:
                    return new Run(inline.ToString() ?? string.Empty);
            }
        }

        private Hyperlink CreateLink(string target, string? title)
        {
            var hyperlink = new Hyperlink { Foreground = _accent, TextDecorations = TextDecorations.Underline };
            ToolTipService.SetToolTip(hyperlink, string.IsNullOrWhiteSpace(title) ? target : title + "\n" + target);
            hyperlink.Click += (_, args) => { args.Handled = true; _navigate(target); };
            return hyperlink;
        }

        private WpfBlock RenderCode(string text, string info)
        {
            var language = info.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var section = new Section
            {
                Background = _codeBackground, BorderBrush = _border, BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 4, 0, 16)
            };
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 7) };
            // Header controls use their own text metrics instead of the document's line spacing.
            TextBlock.SetLineHeight(header, double.NaN);
            var copy = new Button
            {
                Content = "Copy", FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                MinHeight = 24, Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = _surface, Foreground = Text,
                BorderBrush = _border, BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Arrow, ToolTip = "Copy code block"
            };
            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(text);
                    copy.Content = "Copied";
                }
                catch (ExternalException)
                {
                    copy.Content = "Clipboard busy";
                }
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); copy.Content = "Copy"; };
                timer.Start();
            };
            DockPanel.SetDock(copy, Dock.Right);
            header.Children.Add(copy);
            header.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(language) ? "Code" : language,
                Foreground = _muted, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            section.Blocks.Add(new BlockUIContainer(header) { Margin = new Thickness(0) });
            var paragraph = new Paragraph
            {
                FontFamily = new FontFamily("Consolas"), FontSize = _fontSize * .9,
                Foreground = Text, LineHeight = _fontSize * 1.45, Margin = new Thickness(0)
            };
            foreach (var inline in SyntaxHighlighter.CreateInlines(text.TrimEnd('\r', '\n'), language, _dark))
                paragraph.Inlines.Add(inline);
            section.Blocks.Add(paragraph);
            return section;
        }

        private WpfTable RenderTable(Markdig.Extensions.Tables.Table source)
        {
            var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 5, 0, 16), FontSize = _fontSize * .94 };
            foreach (var _ in source.ColumnDefinitions)
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var rows = new TableRowGroup();
            table.RowGroups.Add(rows);
            foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
            {
                var rendered = new WpfTableRow();
                rows.Rows.Add(rendered);
                var column = 0;
                foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                {
                    var renderedCell = new WpfTableCell
                    {
                        Padding = new Thickness(10, 7, 10, 7),
                        BorderBrush = _border, BorderThickness = new Thickness(0, 0, 0, 1),
                        ColumnSpan = Math.Max(1, cell.ColumnSpan), RowSpan = Math.Max(1, cell.RowSpan)
                    };
                    if (row.IsHeader)
                    {
                        renderedCell.FontWeight = FontWeights.SemiBold;
                        renderedCell.Background = _surface;
                    }
                    var columnIndex = cell.ColumnIndex >= 0 ? cell.ColumnIndex : column;
                    if (columnIndex < source.ColumnDefinitions.Count)
                        renderedCell.TextAlignment = source.ColumnDefinitions[columnIndex].Alignment switch
                        {
                            TableColumnAlign.Center => TextAlignment.Center,
                            TableColumnAlign.Right => TextAlignment.Right,
                            _ => TextAlignment.Left
                        };
                    foreach (var block in cell)
                    {
                        var content = RenderBlock(block);
                        content.Margin = new Thickness(0);
                        renderedCell.Blocks.Add(content);
                    }
                    if (renderedCell.Blocks.Count == 0) renderedCell.Blocks.Add(new Paragraph());
                    rendered.Cells.Add(renderedCell);
                    column += renderedCell.ColumnSpan;
                }
            }
            return table;
        }

        private WpfInline RenderImage(LinkInline link)
        {
            var alt = InlineText(link);
            var source = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
            var fallback = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(alt) ? "Image unavailable" : "Image: " + alt,
                Foreground = _muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
                FontSize = _fontSize, Margin = new Thickness(6)
            };
            var holder = new Border { Child = fallback, MaxWidth = 900, Margin = new Thickness(0, 7, 0, 7) };
            var widthBinding = new MultiBinding { Converter = ImageWidthConverter.Instance, Mode = BindingMode.OneWay };
            widthBinding.Bindings.Add(new Binding(nameof(FlowDocument.PageWidth)) { Source = Document });
            widthBinding.Bindings.Add(new Binding(nameof(FlowDocument.PagePadding)) { Source = Document });
            holder.SetBinding(FrameworkElement.MaxWidthProperty, widthBinding);
            ToolTipService.SetToolTip(holder, string.IsNullOrWhiteSpace(link.Title) ? source : link.Title);
            try
            {
                Uri uri;
                if (Uri.TryCreate(source, UriKind.Absolute, out var absolute))
                    uri = absolute;
                else
                    uri = new Uri(Path.GetFullPath(Path.Combine(_directory, Uri.UnescapeDataString(source))));
                if (uri.Scheme is not ("http" or "https" or "file"))
                    return new InlineUIContainer(holder);

                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    MaxWidth = 900, MaxHeight = 900, HorizontalAlignment = HorizontalAlignment.Left
                };
                System.Windows.Automation.AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(alt) ? "Image" : alt);
                image.ImageFailed += (_, _) => holder.Child = fallback;
                if (uri.IsFile)
                {
                    using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    LoadImage(stream, image);
                    holder.Child = image;
                }
                else
                {
                    _ = LoadRemoteImageAsync(uri, image, holder, fallback);
                }
            }
            catch (Exception error) when (IsImageError(error))
            {
                holder.Child = fallback;
            }
            return new InlineUIContainer(holder);
        }

        private static void LoadImage(Stream stream, Image image)
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            image.MaxWidth = Math.Min(900, frame.Width);
            image.MaxHeight = Math.Min(900, frame.Height);
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            if (width > 1600 || height > 1600)
            {
                if (width >= height) bitmap.DecodePixelWidth = 1600;
                else bitmap.DecodePixelHeight = 1600;
            }
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image.Source = bitmap;
        }

        private static async Task LoadRemoteImageAsync(Uri uri, Image image, Border holder, TextBlock fallback)
        {
            try
            {
                using var stream = new MemoryStream(await ImageClient.GetByteArrayAsync(uri));
                LoadImage(stream, image);
                holder.Child = image;
            }
            catch (Exception error) when (IsImageError(error) || error is HttpRequestException or OperationCanceledException)
            {
                holder.Child = fallback;
            }
        }

        private static bool IsImageError(Exception error) =>
            error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or COMException;

        private static string InlineText(ContainerInline? container)
        {
            if (container is null) return string.Empty;
            var result = new StringBuilder();
            foreach (var inline in container)
            {
                result.Append(inline switch
                {
                    LiteralInline literal => literal.Content.ToString(),
                    CodeInline code => code.Content,
                    HtmlEntityInline entity => entity.Transcoded.ToString(),
                    LineBreakInline => " ",
                    AutolinkInline link => link.Url,
                    ContainerInline child => InlineText(child),
                    _ => string.Empty
                });
            }
            return result.ToString();
        }

        private static SolidColorBrush Brush(string color)
        {
            var result = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            result.Freeze();
            return result;
        }
    }

    private sealed class ImageWidthConverter : IMultiValueConverter
    {
        public static ImageWidthConverter Instance { get; } = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values is [double width, Thickness padding] && double.IsFinite(width))
                return Math.Clamp(width - padding.Left - padding.Right - 8, 48, 900);
            return 640d;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
