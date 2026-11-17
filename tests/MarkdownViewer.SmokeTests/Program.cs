using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MarkdownViewer;
using MarkdownViewer.Models;
using MarkdownViewer.Rendering;
using MarkdownViewer.Services;
using WpfList = System.Windows.Documents.List;

internal static partial class Program
{
    private static readonly List<string> Failures = [];
    private static string _fixtures = string.Empty;
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (TryHandleServiceRegressionChild(args, out var serviceExitCode)) return serviceExitCode;
        if (args.FirstOrDefault() == "--pipe-child")
        {
            using var secondary = new SingleInstanceService();
            if (secondary.IsPrimary) return 3;
            var sent = secondary.SendAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            Console.WriteLine(sent ? "FORWARDED" : "FAILED");
            return sent ? 0 : 4;
        }

        _fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtures);
        Environment.SetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR", Path.Combine(_fixtures, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { app.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/MarkdownViewer;component/UI/Styles.xaml", UriKind.Relative)); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }

        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            if (!args.Contains("--layout-only", StringComparer.Ordinal))
            {
            await Check("Renderer: headings, Cyrillic anchors and duplicate headings", TestHeadings);
            await Check("Renderer: tables, lists, tasks and selectable code", TestMarkdown);
            await Check("Renderer: native hyperlink navigation", TestLinks);
            await Check("Renderer: local images fit the document width", TestImage);
            await Check("Search: several matches in a single Run and background restoration", TestRunSearch);
            await Check("Search: phrases crossing bold/code and paragraph boundaries", TestPhraseSearch);
            await Check("Search: match cap and embedded control exclusion", TestSearchBoundaries);
            await Check("Settings: hidden outline and preferences persist", TestSettings);
            await Check("Files: UTF-16 decoding", TestEncoding);
            await Check("Single instance: separate process forwards files", TestSingleInstance);
            await Check("Window: resources, tabs, duplicate files, reload and hidden outline", TestWindow);
            await Check("Window: application labels are English", TestEnglishInterface);
            await Check("Keyboard: reader scrolling and search field editing", TestReaderKeyboard);
            await Check("Session: tab order, active document and per-file positions survive restart", TestSessionRoundTrip);
            await Check("Tabs: drag reordering, cancellation and session persistence", TestTabReordering);
            await Check("Tabs: reentrant drag capture and insertion indicator cleanup", TestTabDragIndicatorLifetime);
            await Check("Session: closed tabs, missing files and explicit launch files", TestSessionChanges);
            await Check("Session: legacy settings and interrupted startup preserve state", TestSessionCompatibility);
            await Check("Scrolling: smooth progression, repeat, reversal and cancellation", TestSmoothScrolling);
            await Check("Keyboard: Ctrl+F toggles search and clears closed search state", TestSearchToggleKeyboard);
            await Check("Keyboard: Escape closes search before the window; Ctrl+Q quits", TestExitKeyboard);
            await Check("Title bar: compact caption buttons, Snap hit testing and window states", TestNativeTitleBar);
            await Check("Files: invalid documents are handled during opening, reload and session restore", TestInvalidDocuments);
            await Check("Renderer: small images retain their natural size", TestImageNaturalSize);
            await Check("Layout: wide outline fits a narrow window", TestWideOutlineNarrowWindow);
            await Check("Watcher: updates resume after a directory is recreated", TestWatcherRecovery);
            await Check("Search: switching tabs and immediately scrolling preserves results", TestSearchAfterInterruptedRestore);
            await Check("Single instance: a new primary can replace an exited process", TestSingleInstanceRecovery);
            }
            await Check("Window: light, dark and narrow layout render", TestWindowRendering);
            await Check("Theme: dark menus, submenus and scrollbars", TestDarkControls);
            Console.WriteLine($"RESULT: {_passed} passed, {Failures.Count} failed");
            foreach (var failure in Failures) Console.Error.WriteLine(failure);
            app.Shutdown(Failures.Count == 0 ? 0 : 1);
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
        Dispatcher.Run();
        return Failures.Count == 0 ? 0 : 1;
    }

    private static async Task Check(string name, Func<Task> test)
    {
        try
        {
            var duration = Stopwatch.StartNew();
            await test();
            _passed++;
            Console.WriteLine($"PASS {name} ({duration.ElapsedMilliseconds} ms)");
        }
        catch (Exception error)
        {
            var failure = "FAIL " + name + Environment.NewLine + error;
            Failures.Add(failure);
            Console.Error.WriteLine(failure);
        }
    }

    private static Task TestHeadings()
    {
        var rendered = Render("# Привет, мир!\n\n# Привет, мир!\n\n## **Второй** раздел\n");
        Equal(3, rendered.Headings.Count, "Heading count");
        Equal("привет-мир", rendered.Headings[0].Anchor, "Cyrillic anchor");
        Equal("привет-мир-1", rendered.Headings[1].Anchor, "Duplicate anchor");
        Equal("Второй раздел", rendered.Headings[2].Title, "Formatted heading title");
        Equal(2, rendered.Headings[2].Level, "Heading depth");
        Assert(rendered.Headings.All(heading => heading.Block.Parent is FlowDocument), "Headings belong to the FlowDocument");
        return Task.CompletedTask;
    }

    private static Task TestMarkdown()
    {
        const string markdown = """
            # Example

            | Name | Value |
            | ---- | ----: |
            | sample | 42 |

            - first
            - second

            3. third
            4. fourth

            - [x] done
            - [ ] pending

            > A quote

            ```csharp
            Console.WriteLine("hello");
            ```

            <script>test</script>
            """;
        var rendered = Render(markdown);
        var blocks = Blocks(rendered.Document.Blocks).ToList();
        var table = blocks.OfType<Table>().Single();
        Equal(2, table.Columns.Count, "Table column count");
        Equal(2, table.RowGroups[0].Rows.Count, "Table header and body");
        Assert(blocks.OfType<WpfList>().Any(list => list.MarkerStyle == TextMarkerStyle.Decimal && list.StartIndex == 3), "Ordered list start preserved");
        Assert(blocks.OfType<WpfList>().Count() >= 3, "Ordinary, ordered and task lists are native lists");
        var text = DocumentText(rendered.Document);
        Assert(text.Contains('☑') && text.Contains('☐') && text.Contains("done") && text.Contains("pending"), "Read-only task states are visible text: " + text);
        Assert(text.Contains("Console.WriteLine(\"hello\");"), "Code remains selectable native text");
        Assert(text.Contains("<script>test</script>"), "Raw HTML remains text");
        Assert(blocks.OfType<BlockUIContainer>().Any(container => container.Child is DockPanel panel && panel.Children.OfType<Button>().Any()), "Copy button available");
        return Task.CompletedTask;
    }

    private static Task TestLinks()
    {
        string? target = null;
        var rendered = new MarkdownDocumentRenderer().Render("[guide](guide.md#start)", Fixture("readme.md"), false, 16, value => target = value);
        var hyperlink = ((Paragraph)rendered.Document.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
        hyperlink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Equal("guide.md#start", target, "Hyperlink passes its full target");
        return Task.CompletedTask;
    }

    private static Task TestRunSearch()
    {
        var background = Brushes.LightBlue;
        var document = new FlowDocument(new Paragraph(new Run("alpha alpha ALPHA") { Background = background }));
        var search = new DocumentSearchService();
        var matches = search.Find(document, "alpha", false);
        Equal(3, matches.Count, "All matches in a Run");
        foreach (var match in matches)
            Assert(string.Equals("alpha", new TextRange(match.Start, match.End).Text, StringComparison.OrdinalIgnoreCase), "Correct range after Run splitting");
        search.Clear();
        foreach (var match in matches)
        {
            var brush = new TextRange(match.Start, match.End).GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;
            Equal(background.Color, brush?.Color, "Original background restored");
        }
        Equal(3, search.Find(document, "ALPHA", true).Count, "Repeated search count");
        Equal(0, search.Find(document, "missing", false).Count, "Changing query clears previous matches");
        Equal("alpha alpha ALPHA\r\n", DocumentText(document), "Searching never changes text");
        return Task.CompletedTask;
    }

    private static async Task TestImage()
    {
        var imagePath = Fixture("wide-image.png");
        var bitmap = BitmapSource.Create(1600, 900, 96, 96, PixelFormats.Bgra32, null, new byte[1600 * 900 * 4], 1600 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(imagePath)) encoder.Save(stream);
        var rendered = Render("![Wide local image](wide-image.png)");
        var container = ((Paragraph)rendered.Document.Blocks.FirstBlock).Inlines.OfType<InlineUIContainer>().Single();
        var holder = (Border)container.Child;
        Assert(holder.Child is Image, "Local image decodes successfully");
        rendered.Document.PageWidth = 400;
        rendered.Document.PagePadding = new Thickness(24);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Equal(344d, holder.MaxWidth, "Image width follows available page width");
    }

    private static Task TestPhraseSearch()
    {
        var document = Render("Open **README** and `code` now.").Document;
        var search = new DocumentSearchService();
        var matches = search.Find(document, "Open README and code", false);
        Equal(1, matches.Count, "Phrase across bold and code");
        Equal("Open README and code", new TextRange(matches[0].Start, matches[0].End).Text, "Phrase selection");
        search.Clear();

        var paragraphs = new FlowDocument(new Paragraph(new Run("tail")));
        paragraphs.Blocks.Add(new Paragraph(new Run("head")));
        matches = search.Find(paragraphs, "tail\nhead", false);
        Equal(1, matches.Count, "Phrase across paragraph break");
        Equal("tail\nhead", new TextRange(matches[0].Start, matches[0].End).Text.Replace("\r\n", "\n", StringComparison.Ordinal), "Paragraph search selection");
        search.Clear();
        return Task.CompletedTask;
    }

    private static Task TestSearchBoundaries()
    {
        var search = new DocumentSearchService();
        var document = new FlowDocument(new Paragraph(new Run(string.Join(' ', Enumerable.Repeat("a", 10_005)))));
        var matches = search.Find(document, "a", false);
        Equal(10_000, matches.Count, "Search match cap");
        Assert(search.Truncated, "Cap is reported to UI");
        Assert(search.HighlightsLimited, "Background highlighting is bounded independently of navigation");
        Equal("a", new TextRange(matches[^1].Start, matches[^1].End).Text, "Last non-highlighted match remains selectable");
        search.Clear();
        Assert(!search.Truncated, "Clear resets cap state");
        var rendered = Render("```csharp\nvar x = 1;\n```");
        Equal(0, search.Find(rendered.Document, "Copy", false).Count, "Copy controls are excluded from search");
        return Task.CompletedTask;
    }

    private static Task TestSettings()
    {
        var store = new SettingsStore();
        var settings = new AppSettings { OutlineVisible = false, Theme = "Dark", FontSize = 18, ReadingWidth = 640 };
        store.AddRecent(settings, Fixture("readme.md"));
        store.AddRecent(settings, Fixture("readme.md").ToUpperInvariant());
        store.Save(settings);
        Assert(store.LastError is null, "Settings save succeeds");
        var restored = new SettingsStore().Load();
        Assert(!restored.OutlineVisible, "Hidden outline restored");
        Equal("Dark", restored.Theme, "Theme restored");
        Equal(18d, restored.FontSize, "Font size restored");
        Equal(640d, restored.ReadingWidth, "Reading width restored");
        Equal(1, restored.RecentFiles.Count, "Recents deduplicate Windows paths");
        return Task.CompletedTask;
    }

    private static async Task TestEncoding()
    {
        var path = Fixture("unicode.md");
        await File.WriteAllTextAsync(path, "# Привет\nТекст", Encoding.Unicode);
        var document = await new DocumentFileService().LoadAsync(path, CancellationToken.None);
        Equal("# Привет\nТекст", document.Text, "UTF-16 text decoded");
        Assert(document.EncodingName.Contains("16", StringComparison.Ordinal), "Encoding reported");
    }

    private static async Task TestSingleInstance()
    {
        var previousSettingsDirectory = Environment.GetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR");
        Environment.SetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR", Fixture("pipe-settings"));
        try
        {
        using var primary = new SingleInstanceService();
        Assert(primary.IsPrimary, "Fixture owns the primary instance");
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(paths => received.TrySetResult(paths));
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--pipe-child");
        start.ArgumentList.Add(Fixture("first.md"));
        start.ArgumentList.Add(Fixture("Второй файл.md"));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start child fixture");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(timeout.Token);
            var output = await child.StandardOutput.ReadToEndAsync();
            var error = await child.StandardError.ReadToEndAsync();
            Equal(0, child.ExitCode, "Secondary process exit: " + output + error);
            var paths = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(2, paths.Length, "Forwarded file count");
            Equal(Fixture("Второй файл.md"), paths[1], "Unicode/spaces preserved in IPC");
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        }
        finally { Environment.SetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR", previousSettingsDirectory); }
    }

    private static async Task TestWindow()
    {
        var first = Fixture("first.md");
        var second = Fixture("second.md");
        await File.WriteAllTextAsync(first, "# First document\n\nRead **this guide**.");
        await File.WriteAllTextAsync(second, "# Second document\n\nOther content.");
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = 0, Top = 0 };
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Measure(new Size(1100, 780));
            window.Arrange(new Rect(0, 0, 1100, 780));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var toggle = (ToggleButton)window.FindName("OutlineToggle");
            var outline = (FrameworkElement)window.FindName("OutlinePanel");
            Assert(toggle.IsChecked == false && outline.Visibility == Visibility.Collapsed, "Window restores hidden outline");
            await window.OpenFilesAsync([first, second]);
            Equal(2, window.Tabs.Count, "Two tabs opened");
            await window.OpenFilesAsync([first.ToUpperInvariant()]);
            Equal(2, window.Tabs.Count, "Duplicate file activates existing tab");
            var tabs = (ListBox)window.FindName("TabsList");
            Equal(window.Tabs[0], tabs.SelectedItem, "Existing tab selected");
            var reader = (RichTextBox)window.FindName("Reader");
            Assert(DocumentText(reader.Document).Contains("First document"), "Selected document rendered");

            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(Visibility.Visible, outline.Visibility, "Outline opens");
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(500);
            Assert(!new SettingsStore().Load().OutlineVisible, "Outline toggle saved by debounce timer");

            await File.WriteAllTextAsync(first, "# Reloaded document\n\nUpdated externally.");
            var timeout = Stopwatch.StartNew();
            while (!window.Tabs[0].Text.Contains("Reloaded document") && timeout.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(40);
            Assert(window.Tabs[0].Text.Contains("Reloaded document"), "File watcher reloads external edits");
            Assert(DocumentText(reader.Document).Contains("Reloaded document"), "Active document refreshes after file edit");
        }
        finally { window.Close(); }

        var reopened = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = 0, Top = 0 };
        try
        {
            reopened.Measure(new Size(1100, 780));
            reopened.Arrange(new Rect(0, 0, 1100, 780));
            Assert(((ToggleButton)reopened.FindName("OutlineToggle")).IsChecked == false, "Next window keeps outline hidden");
        }
        finally { reopened.Close(); }
    }

    private static RenderedMarkdown Render(string markdown) => new MarkdownDocumentRenderer().Render(markdown, Fixture("readme.md"), false, 16, _ => { });
    private static string Fixture(string name) => Path.Combine(_fixtures, name);
    private static string DocumentText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;

    private static async Task TestEnglishInterface()
    {
        new SettingsStore().Save(new AppSettings());
        var window = await PrepareWindow();
        try
        {
            foreach (var (controlName, accessibleName, shortcut) in new[]
            {
                ("OpenButton", "Open files", "Ctrl+O"),
                ("OutlineToggle", "Table of contents", "Ctrl+Shift+O"),
                ("FindButton", "Find in document", "Ctrl+F")
            })
            {
                var control = (Control)window.FindName(controlName);
                Equal(accessibleName, AutomationProperties.GetName(control), "Icon button has an English accessible name");
                Equal($"{accessibleName} ({shortcut})", control.ToolTip?.ToString(), "Icon button tooltip names the action and shortcut");
            }
            var moreButton = (Button)window.FindName("MoreButton");
            Invoke(window, "More_Click", moreButton, new RoutedEventArgs(ButtonBase.ClickEvent));
            var menu = moreButton.ContextMenu ?? throw new InvalidOperationException("More menu opens");
            try
            {
                var theme = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Theme");
                Equal("System", theme.Items.OfType<MenuItem>().Single(item => item.IsChecked).Header?.ToString(), "System theme label is English");
                theme.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Dark")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            finally { menu.IsOpen = false; }
            var visibleText = VisualDescendants<TextBlock>((DependencyObject)window.Content).Select(block => block.Text)
                .Concat(VisualDescendants<ButtonBase>((DependencyObject)window.Content).Select(button => button.Content?.ToString() ?? ""));
            Assert(!visibleText.Any(text => text.Any(character => character is >= '\u0400' and <= '\u04FF')), "Application chrome contains no Cyrillic labels");
        }
        finally { window.Close(); }
        Equal("Dark", new SettingsStore().Load().Theme, "Theme selected from More menu is saved");
    }

    private static async Task TestReaderKeyboard()
    {
        new SettingsStore().Save(new AppSettings());
        var path = Fixture("keyboard.md");
        await File.WriteAllTextAsync(path, "# Keyboard navigation\n\n" + string.Join("\n\n", Enumerable.Range(1, 160).Select(index => $"Paragraph {index}: a long document to exercise reader navigation.")));
        var window = await PrepareWindow();
        try
        {
            await window.OpenFilesAsync([path]);
            await LayoutWindow(window);
            var reader = (RichTextBox)window.FindName("Reader");
            reader.ScrollToHome();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(Shortcut(window, Key.Down, ModifierKeys.None, reader), "Down handled by reader");
            await WaitForScrolling(window);
            var lineOffset = reader.VerticalOffset;
            Assert(lineOffset > 0, "Down scrolls a line");
            Assert(Shortcut(window, Key.Up, ModifierKeys.None, reader), "Up handled by reader");
            await WaitForScrolling(window);
            Assert(reader.VerticalOffset < lineOffset, "Up scrolls toward the beginning");
            Assert(Shortcut(window, Key.PageDown, ModifierKeys.None, reader), "PageDown handled");
            await WaitForScrolling(window);
            var pageOffset = reader.VerticalOffset;
            Assert(pageOffset > lineOffset * 2, "PageDown advances more than one line");
            Assert(Shortcut(window, Key.PageUp, ModifierKeys.None, reader), "PageUp handled");
            await WaitForScrolling(window);
            Assert(reader.VerticalOffset < pageOffset, "PageUp scrolls backward");
            Assert(Shortcut(window, Key.Space, ModifierKeys.None, window.FindName("OpenButton") as IInputElement), "Space scrolls while a toolbar button has focus");
            await WaitForScrolling(window);
            Assert(reader.VerticalOffset > lineOffset, "Space advances the document");

            var beforeCaptionKeys = reader.VerticalOffset;
            foreach (var name in new[] { "MinimizeButton", "MaximizeButton", "CloseWindowButton" })
                Assert(!Shortcut(window, Key.Space, ModifierKeys.None, (IInputElement)window.FindName(name)), "Space is left to the caption button: " + name);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Equal(beforeCaptionKeys, reader.VerticalOffset, "Caption button activation does not scroll the document");

            Invoke(window, "ShowSearch");
            var search = (TextBox)window.FindName("SearchBox");
            var before = reader.VerticalOffset;
            foreach (var key in new[] { Key.Space, Key.Up, Key.Down, Key.PageUp, Key.PageDown })
                Assert(!Shortcut(window, key, ModifierKeys.None, search), key + " is left to the search input");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Equal(before, reader.VerticalOffset, "Search field navigation does not move the document");
        }
        finally { window.Close(); }
    }

    private static async Task TestSessionRoundTrip()
    {
        var store = new SettingsStore();
        store.Save(new AppSettings());
        var paths = new[] { Fixture("session-first.md"), Fixture("session-second.md"), Fixture("session-third.md") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, LongDocument(Path.GetFileName(path)));
        var expected = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var window = await PrepareWindow();
        try
        {
            await window.RestoreSessionAsync([]);
            await window.OpenFilesAsync(paths);
            var tabs = (ListBox)window.FindName("TabsList");
            var reader = (RichTextBox)window.FindName("Reader");
            for (var index = 0; index < paths.Length; index++)
            {
                tabs.SelectedItem = window.Tabs[index];
                await LayoutWindow(window);
                reader.ScrollToVerticalOffset(300 + index * 450);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                expected[paths[index]] = reader.VerticalOffset;
                Assert(reader.VerticalOffset > 200, "Each document has an independent nonzero position");
            }
            tabs.SelectedItem = window.Tabs[1];
            await LayoutWindow(window);
        }
        finally { window.Close(); }

        var saved = store.Load();
        Assert(paths.SequenceEqual(saved.OpenDocuments.Select(document => document.FilePath)), "Saved session keeps tab order");
        Equal(paths[1], saved.ActiveDocumentPath, "Saved active document");
        foreach (var document in saved.OpenDocuments)
            Near(expected[document.FilePath], document.ScrollOffset, 1, "Saved per-document position");

        var reopened = await PrepareWindow();
        try
        {
            await reopened.RestoreSessionAsync([]);
            await LayoutWindow(reopened);
            var tabs = (ListBox)reopened.FindName("TabsList");
            var reader = (RichTextBox)reopened.FindName("Reader");
            Assert(paths.SequenceEqual(reopened.Tabs.Select(tab => tab.FilePath)), "Restored tabs keep order");
            Equal(reopened.Tabs[1], tabs.SelectedItem, "Saved active tab selected after startup");
            Near(expected[paths[1]], reader.VerticalOffset, 2, "Active position restored after layout");
            foreach (var tab in reopened.Tabs)
            {
                tabs.SelectedItem = tab;
                await LayoutWindow(reopened);
                Near(expected[tab.FilePath], reader.VerticalOffset, 2, "Inactive document restores its own position on activation");
            }

            tabs.SelectedItem = reopened.Tabs[0];
            tabs.SelectedItem = reopened.Tabs[2];
            tabs.SelectedItem = reopened.Tabs[0];
            await LayoutWindow(reopened);
            Near(expected[paths[0]], reader.VerticalOffset, 2, "Rapid selection restores the final tab position");
            tabs.SelectedItem = reopened.Tabs[2];
            await LayoutWindow(reopened);
            Near(expected[paths[2]], reader.VerticalOffset, 2, "Rapid selection does not erase a pending tab position");
        }
        finally { reopened.Close(); }
    }

    private static async Task TestTabReordering()
    {
        var store = new SettingsStore();
        store.Save(new AppSettings { WindowWidth = 1100, WindowHeight = 780 });
        var paths = new[] { Fixture("drag-a.md"), Fixture("drag-b.md"), Fixture("drag-c.md") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, LongDocument(Path.GetFileName(path)));
        var expectedPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var expectedOrder = new[] { paths[0], paths[2], paths[1] };
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = 60, Top = 60 };
        try
        {
            window.Show();
            await window.OpenFilesAsync(paths);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var tabs = (ListBox)window.FindName("TabsList");
            var reader = (RichTextBox)window.FindName("Reader");
            var documents = window.Tabs.ToArray();
            for (var index = 0; index < documents.Length; index++)
            {
                tabs.SelectedItem = documents[index];
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                reader.ScrollToVerticalOffset(280 + index * 360);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                expectedPositions[paths[index]] = reader.VerticalOffset;
                Assert(reader.VerticalOffset > 200, "Drag fixture has an independent reading position");
            }
            tabs.SelectedItem = documents[1];
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var displayedDocument = reader.Document;
            var controller = typeof(MainWindow).GetField("_tabReorder", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                ?? throw new InvalidOperationException("Window owns the tab drag controller");

            ListBoxItem Container(object document) => (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromItem(document);
            Point Edge(object document, bool right) => Container(document).TranslatePoint(
                new Point(right ? Container(document).ActualWidth - 1 : 1, Container(document).ActualHeight / 2), tabs);
            var previewCaptured = false;
            void Drag(object document, Point destination)
            {
                Assert((bool)Invoke(controller, "BeginDrag", document)!, "A tab can begin a drag");
                Equal(Container(document), Mouse.Captured, "The tab owns capture without ListBox selection auto-scrolling");
                var layer = AdornerLayer.GetAdornerLayer(tabs);
                var indicator = layer?.GetAdorners(tabs)?.SingleOrDefault(adorner => adorner.GetType().Name == "InsertionAdorner")
                    ?? throw new InvalidOperationException("Dragging exposes an insertion indicator");
                Assert(!indicator.IsHitTestVisible, "Insertion indicator does not intercept pointer or title-bar input");
                Invoke(controller, "UpdateDrag", destination);
                Equal(new Rect(tabs.RenderSize).Contains(destination) ? Visibility.Visible : Visibility.Hidden,
                    indicator.Visibility, "Insertion indicator follows the valid drop area");
                if (!previewCaptured)
                {
                    var status = (TextBlock)window.FindName("StatusText");
                    var originalStatus = status.Text;
                    status.Text = Path.GetFileName(originalStatus);
                    window.UpdateLayout();
                    Capture(window, "tab-drag.png", window.Background);
                    status.Text = originalStatus;
                    previewCaptured = true;
                }
                Invoke(controller, "CompleteDrag", destination);
                Assert(Mouse.Captured is null, "Completing a drag releases capture");
                Assert(layer!.GetAdorners(tabs)?.Contains(indicator) != true, "Completing or cancelling a drag removes its indicator");
                window.UpdateLayout();
                Equal(documents[1], tabs.SelectedItem, "Reordering preserves selected document identity");
                Assert(ReferenceEquals(displayedDocument, reader.Document), "Reordering does not reload the displayed document");
                Near(expectedPositions[paths[1]], reader.VerticalOffset, 2, "Reordering preserves the current reading position");
            }

            var firstContainer = Container(documents[0]);
            var label = VisualDescendants<TextBlock>(firstContainer).First(block => block.Text == "drag-a.md");
            var closeButton = VisualDescendants<Button>(firstContainer).Single();
            Equal(documents[0], Invoke(controller, "GetTabAt", label), "Tab label is a drag source");
            Assert(Invoke(controller, "GetTabAt", closeButton) is null, "Close button does not begin a tab drag");
            var closeGlyph = VisualDescendants<TextBlock>(closeButton).First();
            Assert(Invoke(controller, "GetTabAt", closeGlyph) is null, "Close glyph does not begin a tab drag");
            var arrow = (RepeatButton)tabs.Template.FindName("TabScrollLeftButton", tabs);
            Assert(Invoke(controller, "GetTabAt", arrow) is null, "Tab scrolling arrow is not a drag source");

            Drag(documents[0], Edge(documents[2], true));
            Assert(new[] { documents[1], documents[2], documents[0] }.SequenceEqual(window.Tabs), "First tab moves after the last tab");
            Drag(documents[0], Edge(documents[1], false));
            Assert(documents.SequenceEqual(window.Tabs), "Last tab moves before the first tab");
            Drag(documents[1], Edge(documents[2], true));
            Assert(expectedOrder.SequenceEqual(window.Tabs.Select(tab => tab.FilePath)), "The selected tab can move while staying active");

            Drag(documents[0], new Point(-10, -10));
            Assert(expectedOrder.SequenceEqual(window.Tabs.Select(tab => tab.FilePath)), "Dropping outside the strip leaves order unchanged");
            Assert((bool)Invoke(controller, "BeginDrag", documents[0])!, "A tab drag begins before capture cancellation");
            Mouse.Capture(null);
            Invoke(controller, "CompleteDrag", Edge(documents[1], true));
            Assert(expectedOrder.SequenceEqual(window.Tabs.Select(tab => tab.FilePath)), "Losing capture cancels the pending reorder");

            window.Width = 700;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var scroller = (ScrollViewer)tabs.Template.FindName("PART_ScrollViewer", tabs);
            window.Width += Container(documents[0]).ActualWidth * 1.5 - scroller.ActualWidth;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            reader.ScrollToVerticalOffset(expectedPositions[paths[1]]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(scroller.ScrollableWidth > 0, "Narrow drag strip has overflowing tabs");
            Rect Viewport() => scroller.TransformToAncestor(tabs).TransformBounds(new Rect(scroller.RenderSize));
            Rect Bounds(object document) => Container(document).TransformToAncestor(tabs).TransformBounds(new Rect(Container(document).RenderSize));
            scroller.ScrollToLeftEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var previousOffset = scroller.HorizontalOffset;
            var viewport = Viewport();
            Assert((bool)Invoke(controller, "BeginDrag", documents[0])!, "Visible tab begins an overflow drag");
            Invoke(controller, "UpdateDrag", new Point(viewport.Right - 1, tabs.ActualHeight / 2));
            var timer = (DispatcherTimer)controller.GetType().GetField("_scrollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
            Assert(timer.IsEnabled, "Right-edge drag starts automatic tab scrolling");
            Invoke(controller, "ScrollTimer_Tick", null, EventArgs.Empty);
            Invoke(controller, "CancelDrag");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(scroller.HorizontalOffset > previousOffset, "Holding a drag at the right edge reveals later tabs");
            Assert(!timer.IsEnabled && Mouse.Captured is null, "Cancelling an edge drag stops scrolling and releases capture");

            previousOffset = scroller.HorizontalOffset;
            viewport = Viewport();
            var visibleDocument = window.Tabs.First(document => Rect.Intersect(Bounds(document), viewport).Width > 20);
            Assert((bool)Invoke(controller, "BeginDrag", visibleDocument)!, "A scrolled visible tab begins a left-edge drag");
            Invoke(controller, "UpdateDrag", new Point(viewport.Left + 1, tabs.ActualHeight / 2));
            Assert(timer.IsEnabled, "Left-edge drag starts automatic tab scrolling");
            Invoke(controller, "ScrollTimer_Tick", null, EventArgs.Empty);
            Invoke(controller, "CancelDrag");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(scroller.HorizontalOffset < previousOffset, "Holding a drag at the left edge reveals earlier tabs");

            scroller.ScrollToLeftEnd();
            scroller.LineRight();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            viewport = Viewport();
            var partialBounds = Bounds(documents[1]);
            Assert(Bounds(documents[0]).Right <= viewport.Left + 1, "A preceding tab is hidden to the left for the scrolled drop regression");
            Assert(partialBounds.Left < viewport.Right && partialBounds.Right > viewport.Right + 1,
                "Dragged tab is partially clipped at the right edge");
            Drag(documents[1], Edge(documents[2], false));
            Assert(documents.SequenceEqual(window.Tabs), "A partially visible tab drops before its neighbour using the scrolled coordinates");
            window.Width = 1100;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            reader.ScrollToVerticalOffset(expectedPositions[paths[1]]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Drag(documents[1], Edge(documents[2], true));
            Assert(expectedOrder.SequenceEqual(window.Tabs.Select(tab => tab.FilePath)), "Final order remains ready for session restoration");

            await WaitUntil(() => expectedOrder.SequenceEqual(store.Load().OpenDocuments.Select(entry => entry.FilePath)),
                "Reordered tabs are automatically saved while the window is open");
            var saved = store.Load();
            Equal(paths[1], saved.ActiveDocumentPath, "Saving the new order preserves the active document");
            foreach (var entry in saved.OpenDocuments)
                Near(expectedPositions[entry.FilePath], entry.ScrollOffset, 2, "Saving the new order preserves every reading position");
        }
        finally { window.Close(); }

        var reopened = await PrepareWindow();
        try
        {
            await reopened.RestoreSessionAsync([]);
            await LayoutWindow(reopened);
            var tabs = (ListBox)reopened.FindName("TabsList");
            var reader = (RichTextBox)reopened.FindName("Reader");
            Assert(expectedOrder.SequenceEqual(reopened.Tabs.Select(tab => tab.FilePath)), "Restart restores the dragged tab order");
            Equal(reopened.Tabs[2], tabs.SelectedItem, "Restart restores the moved active tab");
            foreach (var document in reopened.Tabs)
            {
                tabs.SelectedItem = document;
                await LayoutWindow(reopened);
                Near(expectedPositions[document.FilePath], reader.VerticalOffset, 2, "Restart restores each reordered document's reading position");
            }
        }
        finally { reopened.Close(); }
    }

    private static async Task TestTabDragIndicatorLifetime()
    {
        new SettingsStore().Save(new AppSettings { WindowWidth = 1100, WindowHeight = 780 });
        var paths = new[] { Fixture("indicator-a.md"), Fixture("indicator-b.md"), Fixture("indicator-c.md") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, "# Insertion indicator\n\nDrag cleanup regression.");
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = 60, Top = 60 };
        try
        {
            window.Show();
            await window.OpenFilesAsync(paths);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var tabs = (ListBox)window.FindName("TabsList");
            var controller = typeof(MainWindow).GetField("_tabReorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var layer = AdornerLayer.GetAdornerLayer(tabs)
                ?? throw new InvalidOperationException("Tab strip has an adorner layer");
            var document = window.Tabs[0];
            int IndicatorCount() => layer.GetAdorners(tabs)?.Count(adorner => adorner.GetType().Name == "InsertionAdorner") ?? 0;
            ListBoxItem Container(object tab) => (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromItem(tab);

            void BeginReentrantDrag()
            {
                window.UpdateLayout();
                var item = Container(document);
                var reentered = false;
                MouseEventHandler duringCapture = (_, _) =>
                {
                    if (reentered) return;
                    reentered = true;
                    // Capture can synchronously deliver more input before the outer drag start returns.
                    Invoke(controller, "BeginDrag", document);
                };
                item.AddHandler(Mouse.GotMouseCaptureEvent, duringCapture, true);
                try { Assert((bool)Invoke(controller, "BeginDrag", document)!, "Outer drag starts after synchronous capture callbacks"); }
                finally { item.RemoveHandler(Mouse.GotMouseCaptureEvent, duringCapture); }
                Assert(reentered, "Capture callback reenters the drag startup path");
                Equal(1, IndicatorCount(), "Reentrant drag startup creates exactly one insertion indicator");
            }

            BeginReentrantDrag();
            var last = Container(window.Tabs[^1]);
            var destination = last.TranslatePoint(new Point(last.ActualWidth - 1, last.ActualHeight / 2), tabs);
            Invoke(controller, "UpdateDrag", destination);
            Invoke(controller, "CompleteDrag", destination);
            window.UpdateLayout();
            Equal(document, window.Tabs[^1], "A reentrant drag still completes its requested reorder");
            Equal(0, IndicatorCount(), "Completing a reentrant drag leaves no insertion indicators");

            BeginReentrantDrag();
            Mouse.Capture(null);
            Equal(0, IndicatorCount(), "Losing capture removes the only insertion indicator");

            BeginReentrantDrag();
            var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent,
                Source = Container(document)
            };
            Container(document).RaiseEvent(release);
            Assert(release.Handled, "Mouse release routes through the tab drag handler");
            Equal(0, IndicatorCount(), "Routed mouse release leaves no insertion indicators");
            Assert(Mouse.Captured is null, "Routed mouse release frees pointer capture");

            window.UpdateLayout();
            var cancelledItem = Container(document);
            MouseEventHandler cancelDuringCapture = (_, _) => Mouse.Capture(null);
            cancelledItem.AddHandler(Mouse.GotMouseCaptureEvent, cancelDuringCapture, true);
            try { Assert(!(bool)Invoke(controller, "BeginDrag", document)!, "Capture loss during startup cancels the drag"); }
            finally { cancelledItem.RemoveHandler(Mouse.GotMouseCaptureEvent, cancelDuringCapture); }
            Equal(0, IndicatorCount(), "Cancelled startup cannot attach an insertion indicator after capture loss");
            Assert(Mouse.Captured is null, "Cancelled startup leaves pointer capture released");

            BeginReentrantDrag();
            while (window.Tabs.Count > 0) Invoke(window, "CloseTab", window.Tabs[0]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Equal(0, IndicatorCount(), "Closing all tabs leaves the strip free of insertion indicators");
            Assert(Mouse.Captured is null, "Closing tabs cancels pointer capture");
        }
        finally { window.Close(); }
    }

    private static async Task TestSessionChanges()
    {
        var store = new SettingsStore();
        var first = Fixture("session-survivor.md");
        var second = Fixture("session-other.md");
        var missing = Fixture("missing-session-file.md");
        foreach (var path in new[] { first, second }) await File.WriteAllTextAsync(path, LongDocument(Path.GetFileName(path)));
        store.Save(new AppSettings
        {
            OpenDocuments = [new() { FilePath = first, ScrollOffset = 250 }, new() { FilePath = missing, ScrollOffset = 400 }, new() { FilePath = second, ScrollOffset = 800 }],
            ActiveDocumentPath = missing
        });
        var window = await PrepareWindow();
        try
        {
            await window.RestoreSessionAsync([]);
            await LayoutWindow(window);
            Equal(2, window.Tabs.Count, "Missing document does not prevent valid tabs from opening");
            Assert(new[] { first, second }.SequenceEqual(window.Tabs.Select(tab => tab.FilePath)), "Available tabs keep saved relative order");
            var tabs = (ListBox)window.FindName("TabsList");
            Equal(window.Tabs[0], tabs.SelectedItem, "Missing active document falls back to an available tab");
            Equal(Visibility.Visible, ((FrameworkElement)window.FindName("NoticePanel")).Visibility, "Unavailable documents produce a nonfatal notice");
            Invoke(window, "CloseTab", window.Tabs[1]);
            Equal(1, window.Tabs.Count, "A restored tab can be closed");
        }
        finally { window.Close(); }
        var saved = store.Load();
        Equal(1, saved.OpenDocuments.Count, "Closed and unavailable tabs are not restored again");
        Equal(first, saved.OpenDocuments[0].FilePath, "Remaining tab persisted");

        var restored = await PrepareWindow();
        try
        {
            await restored.RestoreSessionAsync([first.ToUpperInvariant(), second]);
            await LayoutWindow(restored);
            Equal(2, restored.Tabs.Count, "Explicit launch paths merge with the saved session without duplicates");
            Equal(restored.Tabs[1], ((ListBox)restored.FindName("TabsList")).SelectedItem, "An explicit launch file takes focus");
            while (restored.Tabs.Count > 0) Invoke(restored, "CloseTab", restored.Tabs[0]);
        }
        finally { restored.Close(); }
        Equal(0, store.Load().OpenDocuments.Count, "Closing every tab persists an empty session");
        Assert(store.Load().ActiveDocumentPath is null, "Empty session has no active path");
        var empty = await PrepareWindow();
        try { await empty.RestoreSessionAsync([]); Equal(0, empty.Tabs.Count, "An empty session stays empty after restart"); }
        finally { empty.Close(); }
    }

    private static async Task TestSessionCompatibility()
    {
        var store = new SettingsStore();
        var path = Fixture("legacy-position.md");
        await File.WriteAllTextAsync(path, LongDocument("Legacy document"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new
        {
            Theme = "Dark",
            OutlineVisible = false,
            ScrollPositions = new Dictionary<string, double> { [path] = 460 }
        }));
        var legacy = store.Load();
        Equal(0, legacy.OpenDocuments.Count, "Legacy settings default to an empty session");
        Equal("Dark", legacy.Theme, "Existing preferences survive migration");
        var window = await PrepareWindow();
        try
        {
            await window.RestoreSessionAsync([path]);
            await LayoutWindow(window);
            Near(460, ((RichTextBox)window.FindName("Reader")).VerticalOffset, 2, "Explicitly reopened legacy file keeps historical position");
        }
        finally { window.Close(); }

        var many = new AppSettings
        {
            OpenDocuments = Enumerable.Range(1, 120).Select(index => new OpenDocumentState
            {
                FilePath = Fixture($"many-tabs-{index}.md"),
                ScrollOffset = index * 10
            }).ToList()
        };
        store.Save(many);
        var manyRestored = store.Load();
        Equal(120, manyRestored.OpenDocuments.Count, "Open session is independent of the 100-file reading-history limit");
        Near(1200, manyRestored.OpenDocuments[^1].ScrollOffset, 1, "Each open tab retains its position beyond history capacity");

        var missing = Fixture("startup-not-yet-available.md");
        store.Save(new AppSettings
        {
            OpenDocuments = [new() { FilePath = missing, ScrollOffset = 120 }, new() { FilePath = path, ScrollOffset = 700 }],
            ActiveDocumentPath = path
        });
        var interrupted = await PrepareWindow();
        var restore = interrupted.RestoreSessionAsync([]);
        interrupted.Close();
        await restore;
        var after = store.Load();
        Assert(after.OpenDocuments.Any(document => string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase)), "Closing during startup preserves documents that have not been loaded yet");
        Near(700, after.OpenDocuments.Single(document => string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase)).ScrollOffset, 1, "Interrupted startup keeps the pending document position");
    }

    private static async Task TestSmoothScrolling()
    {
        new SettingsStore().Save(new AppSettings());
        var first = Fixture("smooth-first.md");
        var second = Fixture("smooth-second.md");
        foreach (var path in new[] { first, second }) await File.WriteAllTextAsync(path, LongDocument(Path.GetFileName(path)));
        var window = await PrepareWindow();
        try
        {
            await window.OpenFilesAsync([first, second, first]);
            await LayoutWindow(window);
            var reader = (RichTextBox)window.FindName("Reader");
            var tabs = (ListBox)window.FindName("TabsList");
            reader.ScrollToVerticalOffset(0);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var page = Math.Max(25.6, reader.ViewportHeight - 25.6);
            Shortcut(window, Key.PageDown, ModifierKeys.None, reader);
            Near(0, reader.VerticalOffset, 1, "Keyboard scrolling does not jump immediately to the destination");
            await WaitUntil(() => reader.VerticalOffset > 1, "Animation starts advancing the document");
            Assert(reader.VerticalOffset < page - 1, "Animation exposes intermediate offsets before settling");
            await WaitForScrolling(window);
            Near(page, reader.VerticalOffset, 2, "PageDown reaches its intended offset");

            var repeatStart = reader.VerticalOffset;
            for (var index = 0; index < 3; index++) Shortcut(window, Key.Down, ModifierKeys.None, reader);
            await WaitForScrolling(window);
            Near(repeatStart + 3 * 25.6, reader.VerticalOffset, 2, "Rapid repeated keys accumulate their distance");

            var reverseStart = reader.VerticalOffset;
            Shortcut(window, Key.Space, ModifierKeys.None, reader);
            await WaitUntil(() => reader.VerticalOffset > reverseStart + 1, "Forward motion starts before reversal");
            var beforeReverse = reader.VerticalOffset;
            Shortcut(window, Key.PageUp, ModifierKeys.None, reader);
            await WaitForScrolling(window);
            Assert(reader.VerticalOffset < beforeReverse - 1, "Opposite direction responds without finishing the old forward target");

            Shortcut(window, Key.PageDown, ModifierKeys.None, reader);
            tabs.SelectedItem = window.Tabs[1];
            await LayoutWindow(window);
            var secondOffset = reader.VerticalOffset;
            await Task.Delay(400);
            Near(secondOffset, reader.VerticalOffset, 1, "Switching tabs cancels old-document motion");

            Shortcut(window, Key.PageDown, ModifierKeys.None, reader);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent,
                Source = reader
            };
            reader.RaiseEvent(wheel);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var manualOffset = reader.VerticalOffset;
            await Task.Delay(400);
            Near(manualOffset, reader.VerticalOffset, 1, "Manual wheel input cancels keyboard animation");

            Shortcut(window, Key.PageDown, ModifierKeys.None, reader);
            await File.WriteAllTextAsync(second, LongDocument("Reloaded smooth document"));
            await (Task)Invoke(window, "ReloadFileAsync", second)!;
            await LayoutWindow(window);
            var reloadOffset = reader.VerticalOffset;
            await Task.Delay(400);
            Near(reloadOffset, reader.VerticalOffset, 1, "Reload does not leave an animation running on a replaced document");

            reader.ScrollToEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var endOffset = reader.VerticalOffset;
            Shortcut(window, Key.PageDown, ModifierKeys.None, reader);
            await WaitForScrolling(window);
            Near(endOffset, reader.VerticalOffset, 1, "Smooth scrolling is clamped at the end of the document");
            reader.ScrollToHome();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Shortcut(window, Key.Up, ModifierKeys.None, reader);
            await WaitForScrolling(window);
            Near(0, reader.VerticalOffset, 1, "Smooth scrolling is clamped at the beginning of the document");
        }
        finally { window.Close(); }
    }

    private static string LongDocument(string heading) => "# " + heading + "\n\n" + string.Join("\n\n", Enumerable.Range(1, 180).Select(index => $"Paragraph {index}: document text for session and scrolling checks."));

    private static async Task WaitUntil(Func<bool> condition, string message, int timeoutMilliseconds = 2500)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < timeoutMilliseconds) await Task.Delay(10);
        Assert(condition(), message);
    }

    private static async Task WaitForScrolling(MainWindow window)
    {
        var controller = typeof(MainWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(field => field.FieldType.Name == "SmoothScrollController")?.GetValue(window)
            ?? throw new InvalidOperationException("Window exposes its smooth-scroll controller for integration testing");
        var animating = controller.GetType().GetProperty("IsAnimating")
            ?? throw new InvalidOperationException("Scroll controller exposes animation status");
        await WaitUntil(() => !(bool)animating.GetValue(controller)!, "Keyboard animation settles within its duration");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    { Assert(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected:0.##}, actual {actual:0.##}"); }

    private static async Task TestSearchToggleKeyboard()
    {
        var path = Fixture("search-toggle.md");
        await File.WriteAllTextAsync(path, "# Search test\n\nneedle needle");
        var window = await PrepareWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            var panel = (FrameworkElement)window.FindName("SearchPanel");
            var search = (TextBox)window.FindName("SearchBox");
            var count = (TextBlock)window.FindName("SearchCount");
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("_searchTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var matchesField = typeof(MainWindow).GetField("_matches", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert(Shortcut(window, Key.F, ModifierKeys.Control, window), "Ctrl+F is handled without a document");
            Equal(Visibility.Collapsed, panel.Visibility, "Search stays closed without a document");

            await window.OpenFilesAsync([path]);
            Assert(Shortcut(window, Key.F, ModifierKeys.Control, window), "Ctrl+F opens search");
            Equal(Visibility.Visible, panel.Visibility, "Search panel is visible");
            search.Text = "needle";
            await WaitUntil(() => count.Text == "1 of 2", "Search finds both matches");
            var matches = (IReadOnlyList<SearchMatch>)matchesField.GetValue(window)!;
            var firstMatch = new TextRange(matches[0].Start, matches[0].End);
            Assert(firstMatch.GetPropertyValue(TextElement.BackgroundProperty) is Brush, "Search highlights a match");

            Assert(Shortcut(window, Key.F, ModifierKeys.Control, search), "Repeated Ctrl+F closes search");
            Assert(!closed, "Closing search keeps the application open");
            Equal(Visibility.Collapsed, panel.Visibility, "Repeated Ctrl+F hides search");
            Equal(0, ((IReadOnlyList<SearchMatch>)matchesField.GetValue(window)!).Count, "Closing search clears matches");
            Assert(firstMatch.GetPropertyValue(TextElement.BackgroundProperty) is null, "Closing search clears highlighting");

            Shortcut(window, Key.F, ModifierKeys.Control, window);
            Equal(Visibility.Visible, panel.Visibility, "Ctrl+F reopens search");
            Equal("needle", search.Text, "Reopening search preserves the query");
            Equal(2, ((IReadOnlyList<SearchMatch>)matchesField.GetValue(window)!).Count, "Reopening search restores matches");
            search.Text = "needle ";
            Assert(timer.IsEnabled, "Editing search schedules a pending search");
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = search
            };
            search.RaiseEvent(escape);
            Assert(escape.Handled, "Escape from the search field is consumed by the window shortcut handler");
            Assert(!closed && panel.Visibility == Visibility.Collapsed, "Escape hides search without quitting");
            Assert(!timer.IsEnabled, "Closing search cancels its pending timer");
            Equal(0, ((IReadOnlyList<SearchMatch>)matchesField.GetValue(window)!).Count, "Escape clears matches");
        }
        finally { if (!closed) window.Close(); }
    }

    private static async Task TestExitKeyboard()
    {
        var path = Fixture("exit.md");
        await File.WriteAllTextAsync(path, "# Exit test\n\nText.");
        foreach (var (key, modifiers, focusName) in new[]
        {
            (Key.Escape, ModifierKeys.None, ""),
            (Key.Escape, ModifierKeys.None, "SearchBox"),
            (Key.Escape, ModifierKeys.None, "Reader"),
            (Key.Escape, ModifierKeys.None, "OutlineList"),
            (Key.Q, ModifierKeys.Control, ""),
            (Key.Q, ModifierKeys.Control, "SearchBox")
        })
        {
            var window = await PrepareWindow();
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                if (focusName.Length > 0)
                {
                    await window.OpenFilesAsync([path]);
                    Shortcut(window, Key.F, ModifierKeys.Control, window);
                    Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SearchPanel")).Visibility, "Search opened for exit regression");
                }
                Assert(Shortcut(window, key, modifiers, focusName.Length > 0 ? (IInputElement)window.FindName(focusName) : window), key + " handled");
                if (key == Key.Escape && focusName.Length > 0)
                {
                    Assert(!closed, "First Escape keeps the window open when focus is in " + focusName);
                    Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SearchPanel")).Visibility, "First Escape closes search");
                    Assert(Shortcut(window, key, modifiers, window), "Second Escape is handled");
                }
                Assert(closed, key + " closes the window once search is closed, or immediately for Ctrl+Q");
            }
            finally { if (!closed) window.Close(); }
        }

        foreach (var searchVisible in new[] { false, true })
        {
            var menuWindow = await PrepareWindow();
            var menuClosed = false;
            menuWindow.Closed += (_, _) => menuClosed = true;
            try
            {
                if (searchVisible)
                {
                    await menuWindow.OpenFilesAsync([path]);
                    Shortcut(menuWindow, Key.F, ModifierKeys.Control, menuWindow);
                }
                var moreButton = (Button)menuWindow.FindName("MoreButton");
                Invoke(menuWindow, "More_Click", moreButton, new RoutedEventArgs(ButtonBase.ClickEvent));
                var menu = moreButton.ContextMenu;
                Assert(menu is not null, "More menu opens for Escape regression");
                var source = HwndSource.FromHwnd(new WindowInteropHelper(menuWindow).Handle);
                Assert(source is not null, "Window provides a keyboard event source");
                menu!.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = menu
                });
                if (searchVisible)
                {
                    Assert(!menuClosed && !menu.IsOpen, "Escape closes the menu without quitting while search is open");
                    Equal(Visibility.Collapsed, ((FrameworkElement)menuWindow.FindName("SearchPanel")).Visibility, "Escape from the menu also closes search");
                    Shortcut(menuWindow, Key.Escape, ModifierKeys.None, menuWindow);
                }
                Assert(menuClosed, "Escape closes the app once search is closed, including from a menu");
            }
            finally { if (!menuClosed) menuWindow.Close(); }
        }
    }

    private static bool Shortcut(MainWindow window, Key key, ModifierKeys modifiers, IInputElement? focusedElement) =>
        (bool)Invoke(window, "TryHandleShortcut", key, modifiers, focusedElement)!;

    private static async Task TestNativeTitleBar()
    {
        new SettingsStore().Save(new AppSettings { WindowWidth = 1100, WindowHeight = 780, WindowLeft = 60, WindowTop = 60 });
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var titleBar = (FrameworkElement)window.FindName("TitleBar");
            var compactHeight = titleBar.ActualHeight;
            var handle = new WindowInteropHelper(window).Handle;
            var paths = Enumerable.Range(1, 4).Select(index => Fixture($"titlebar-document-{index}-with-a-long-filename.md")).ToArray();
            foreach (var path in paths) await File.WriteAllTextAsync(path, "# Title bar\n\nWindow integration check.");
            await window.OpenFilesAsync(paths);

            foreach (var width in new[] { 1100d, 640d })
            {
                window.Width = width;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Near(compactHeight, titleBar.ActualHeight, 0.5, "Overflowing tabs do not make the title bar taller");
                foreach (var name in new[] { "OpenButton", "RecentButton", "OutlineToggle", "FindButton", "ZoomButton", "MoreButton" })
                {
                    var control = (FrameworkElement)window.FindName(name);
                    Equal(1, NativeHitTest(handle, control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2))), name + " receives client input in the title bar");
                }

                var dragArea = (FrameworkElement)window.FindName("TitleBarDragArea");
                Assert(dragArea.ActualWidth >= 20, "A drag area remains available with overflowing tabs");
                Equal(2, NativeHitTest(handle, dragArea.PointToScreen(new Point(dragArea.ActualWidth / 2, dragArea.ActualHeight / 2))), "Empty title bar provides native window dragging");
                ValidateCaptionButtons(window, handle);

                var tabs = (ListBox)window.FindName("TabsList");
                var selected = (FrameworkElement)tabs.ItemContainerGenerator.ContainerFromItem(tabs.SelectedItem);
                var root = (FrameworkElement)window.Content;
                var viewport = VisualDescendants<ScrollContentPresenter>(tabs).First();
                var stripBounds = viewport.TransformToAncestor(root).TransformBounds(new Rect(viewport.RenderSize));
                stripBounds.Inflate(-3, -3);
                var selectedBounds = selected.TransformToAncestor(root).TransformBounds(new Rect(selected.RenderSize));
                var visibleTab = Rect.Intersect(stripBounds, selectedBounds);
                Assert(!visibleTab.IsEmpty && visibleTab.Width > 20, "Active tab remains visible within the available title-bar space");
                Equal(1, NativeHitTest(handle, root.PointToScreen(new Point(visibleTab.X + visibleTab.Width / 2, visibleTab.Y + visibleTab.Height / 2))), "Visible active tab receives client input");
                var scroller = VisualDescendants<ScrollViewer>(tabs).First();
                Assert(scroller.ScrollableWidth > 0, "Long tab names overflow into horizontal scrolling");
                foreach (var name in new[] { "TabScrollLeftButton", "TabScrollRightButton" })
                {
                    var arrow = (RepeatButton)tabs.Template.FindName(name, tabs);
                    Assert(arrow.IsVisible, "Overflow navigation is visible: " + name);
                    Assert(arrow.IsEnabled, "Overflow navigation receives button input: " + name);
                    Equal(1, NativeHitTest(handle, arrow.PointToScreen(new Point(arrow.ActualWidth / 2, arrow.ActualHeight / 2))), "Tab arrow receives client input: " + name);
                }
                var leftArrow = (RepeatButton)tabs.Template.FindName("TabScrollLeftButton", tabs);
                var previousOffset = scroller.HorizontalOffset;
                leftArrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(scroller.HorizontalOffset < previousOffset, "Left arrow scrolls overflowing tabs without growing the title bar");
                var leftOffset = scroller.HorizontalOffset;
                ((RepeatButton)tabs.Template.FindName("TabScrollRightButton", tabs)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(scroller.HorizontalOffset > leftOffset, "Right arrow scrolls overflowing tabs in the opposite direction");
            }

            Assert(GetWindowRect(handle, out var normalBounds), "Native window bounds are available");
            Equal(10, NativeHitTest(handle, new Point(normalBounds.Left + 1, (normalBounds.Top + normalBounds.Bottom) / 2)), "Left border provides native resizing");
            await ValidateMaximizePointerInput(window, handle);
            ((Button)window.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => window.WindowState == WindowState.Minimized, "Minimize button executes the system command");
            SystemCommands.RestoreWindow(window);
            await WaitUntil(() => window.WindowState == WindowState.Normal, "Native restore command works");
            ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => window.WindowState == WindowState.Maximized, "Maximize button remains available to keyboard and accessibility activation");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            ValidateCaptionButtons(window, handle);

            var monitor = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            Assert(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor), "Monitor work area is available");
            var content = (FrameworkElement)window.Content;
            var topLeft = content.PointToScreen(new Point());
            var bottomRight = content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight));
            Assert(topLeft.X >= monitor.Work.Left - 2 && topLeft.Y >= monitor.Work.Top - 2
                && bottomRight.X <= monitor.Work.Right + 2 && bottomRight.Y <= monitor.Work.Bottom + 2,
                "Maximized content stays inside the monitor work area");

            ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => window.WindowState == WindowState.Normal, "Maximize button restores the maximized window");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(GetWindowRect(handle, out var restoredBounds), "Restored native bounds are available");
            Near(normalBounds.Left, restoredBounds.Left, 2, "Restore preserves the window's horizontal position");
            Near(normalBounds.Top, restoredBounds.Top, 2, "Restore preserves the window's vertical position");
            Near(normalBounds.Right - normalBounds.Left, restoredBounds.Right - restoredBounds.Left, 2, "Restore preserves window width");
            Near(normalBounds.Bottom - normalBounds.Top, restoredBounds.Bottom - restoredBounds.Top, 2, "Restore preserves window height");
            ((Button)window.FindName("CloseWindowButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntil(() => closed, "Close button executes the system command");
        }
        finally { if (!closed) window.Close(); }
    }

    private static async Task ValidateMaximizePointerInput(MainWindow window, IntPtr handle)
    {
        var maximize = (Button)window.FindName("MaximizeButton");
        Point Center() => maximize.PointToScreen(new Point(maximize.ActualWidth / 2, maximize.ActualHeight / 2));
        void Press() => SendMessage(handle, 0x00A1, new IntPtr(9), NativePointParameter(Center()));

        Press();
        Equal(handle, GetCapture(), "Maximize press captures the pointer for an outside release");
        Equal(WindowState.Normal, window.WindowState, "Maximize does not execute on mouse-down");
        var outside = ((FrameworkElement)window.FindName("Reader")).PointToScreen(new Point(20, 20));
        SendClientButtonUp(handle, outside);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Equal(WindowState.Normal, window.WindowState, "Releasing outside maximize cancels the click");
        Assert(GetCapture() != handle, "Outside release clears pointer capture");

        Press();
        SendMessage(handle, 0x001F, IntPtr.Zero, IntPtr.Zero); // WM_CANCELMODE
        SendClientButtonUp(handle, Center());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Equal(WindowState.Normal, window.WindowState, "Cancelled maximize press cannot execute on a later release");
        Assert(GetCapture() != handle, "Cancelling a press clears pointer capture");

        Press();
        SendClientButtonUp(handle, Center());
        await WaitUntil(() => window.WindowState == WindowState.Maximized, "Captured client mouse-up maximizes the window");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Equal(WindowState.Maximized, window.WindowState, "A maximize click executes only once");
        Assert(GetCapture() != handle, "Maximizing clears pointer capture");

        Press();
        SendMessage(handle, 0x00A2, new IntPtr(9), NativePointParameter(Center())); // WM_NCLBUTTONUP
        await WaitUntil(() => window.WindowState == WindowState.Normal, "Non-client mouse-up restores the window");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Equal(WindowState.Normal, window.WindowState, "A restore click executes only once");
        Assert(GetCapture() != handle, "Restoring clears pointer capture");
    }

    private static void SendClientButtonUp(IntPtr handle, Point screenPoint)
    {
        var point = new NativePoint { X = (int)Math.Round(screenPoint.X), Y = (int)Math.Round(screenPoint.Y) };
        Assert(ScreenToClient(handle, ref point), "Captured release converts from screen to client coordinates");
        SendMessage(handle, 0x0202, IntPtr.Zero, NativePointParameter(new Point(point.X, point.Y)));
    }

    private static void ValidateCaptionButtons(MainWindow window, IntPtr handle)
    {
        var header = (FrameworkElement)window.FindName("TitleBar");
        foreach (var (name, hit) in new[] { ("MinimizeButton", 1), ("MaximizeButton", 9), ("CloseWindowButton", 1) })
        {
            var button = (Button)window.FindName(name);
            Near(button.ActualWidth, button.ActualHeight, 0.5, name + " has a square hit target");
            Near(header.ActualHeight, button.ActualHeight, 0.5, name + " fills the title bar height");
            Equal(hit, NativeHitTest(handle, button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2))), name + " preserves its client or Snap input contract");
        }
        var more = (FrameworkElement)window.FindName("MoreButton");
        var minimize = (FrameworkElement)window.FindName("MinimizeButton");
        Assert(more.PointToScreen(new Point(more.ActualWidth, 0)).X <= minimize.PointToScreen(new Point()).X, "Application controls do not overlap caption buttons");
    }

    private static int NativeHitTest(IntPtr handle, Point screenPoint) =>
        (int)SendMessage(handle, 0x0084, IntPtr.Zero, NativePointParameter(screenPoint));

    private static IntPtr NativePointParameter(Point point)
    {
        var coordinates = unchecked((int)(ushort)(short)Math.Round(point.X) | ((int)(ushort)(short)Math.Round(point.Y) << 16));
        return new IntPtr(coordinates);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr handle, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    private static object? Invoke(object target, string method, params object?[] arguments)
    {
        var member = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        try { return member.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static async Task<MainWindow> PrepareWindow()
    {
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = 0, Top = 0 };
        _ = new WindowInteropHelper(window).EnsureHandle();
        await LayoutWindow(window);
        return window;
    }

    private static async Task LayoutWindow(MainWindow window, double width = 1100, double height = 740)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T matching) yield return matching;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static async Task TestDarkControls()
    {
        var store = new SettingsStore();
        store.Save(new AppSettings
        {
            Theme = "Dark", OutlineVisible = true,
            RecentFiles = [Fixture("README.md"), Fixture("installation.md")]
        });
        var window = await PrepareWindow();
        try
        {
            var path = Fixture("scrollbars.md");
            await File.WriteAllTextAsync(path, "# Scrollbars\n\n" + string.Join("\n\n", Enumerable.Range(1, 90).Select(index => "Paragraph " + index)));
            await window.OpenFilesAsync([path]);
            await LayoutWindow(window);
            var reader = (RichTextBox)window.FindName("Reader");
            var actualVertical = VisualDescendants<ScrollBar>(reader).First(bar => bar.Orientation == Orientation.Vertical);
            Assert(actualVertical.Visibility == Visibility.Visible, "Reader exposes a vertical scrollbar");
            ValidateScrollBar(actualVertical, Orientation.Vertical);

            var scroll = new ScrollViewer
            {
                Width = 360, Height = 240,
                Background = (Brush)Application.Current.Resources["WindowBrush"],
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border
                {
                    Width = 800, Height = 700,
                    Background = (Brush)Application.Current.Resources["SidebarBrush"],
                    Child = new TextBlock { Text = "Horizontal and vertical scrolling", Margin = new Thickness(20), Foreground = (Brush)Application.Current.Resources["TextBrush"] }
                }
            };
            scroll.Measure(new Size(360, 240));
            scroll.Arrange(new Rect(0, 0, 360, 240));
            scroll.UpdateLayout();
            var bars = VisualDescendants<ScrollBar>(scroll).ToList();
            Assert(bars.Any(bar => bar.Orientation == Orientation.Horizontal), "Horizontal scrollbar is present");
            foreach (var bar in bars) ValidateScrollBar(bar, bar.Orientation);
            var scrollImage = Capture(scroll, "scrollbars-dark.png", (Brush)Application.Current.Resources["WindowBrush"]);
            AssertPixelDark(scrollImage, scrollImage.PixelWidth - 5, scrollImage.PixelHeight - 5, "Scrollbar intersection is dark");

            foreach (var (method, buttonName, filename) in new[]
            {
                ("Recent_Click", "RecentButton", "menu-recent-dark.png"),
                ("More_Click", "MoreButton", "menu-more-dark.png")
            })
            {
                var button = (Button)window.FindName(buttonName);
                Invoke(window, method, button, new RoutedEventArgs(ButtonBase.ClickEvent));
                var menu = button.ContextMenu ?? throw new InvalidOperationException("Menu is retained on its placement target");
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    menu.ApplyTemplate();
                    menu.Measure(new Size(600, double.PositiveInfinity));
                    menu.Arrange(new Rect(new Point(), menu.DesiredSize));
                    menu.UpdateLayout();
                    var surface = (Border?)menu.Template.FindName("MenuSurface", menu);
                    Assert(surface is not null, "Context menu uses the custom theme template");
                    AssertDark(surface!.Background, "Context menu background follows dark theme");
                    var image = Capture(menu, filename, (Brush)Application.Current.Resources["WindowBrush"]);
                    AssertPixelDark(image, 12, 12, "Menu icon gutter is dark: " + method);
                    if (method == "More_Click")
                    {
                        foreach (var (header, submenuFilename) in new[]
                        {
                            ("Theme", "menu-theme-dark.png"),
                            ("Reading width", "menu-submenu-dark.png")
                        })
                        {
                            var item = menu.Items.OfType<MenuItem>().Single(candidate => candidate.Header?.ToString() == header);
                            if (header == "Theme")
                                Assert(item.Items.OfType<MenuItem>().Single(option => option.IsChecked).Header?.ToString() == "Dark", "Dark theme is visibly checked");
                            item.IsSubmenuOpen = true;
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            item.ApplyTemplate();
                            var popup = (Popup?)item.Template.FindName("PART_Popup", item);
                            Assert(popup?.Child is Border, "Submenu uses the themed popup surface");
                            var submenu = (Border)popup!.Child;
                            submenu.Measure(new Size(600, double.PositiveInfinity));
                            submenu.Arrange(new Rect(new Point(), submenu.DesiredSize));
                            submenu.UpdateLayout();
                            AssertDark(submenu.Background, "Submenu background follows dark theme");
                            var submenuImage = Capture(submenu, submenuFilename, (Brush)Application.Current.Resources["WindowBrush"]);
                            AssertPixelDark(submenuImage, 12, 12, "Submenu icon gutter is dark: " + header);
                            item.IsSubmenuOpen = false;
                        }
                    }
                }
                finally { menu.IsOpen = false; }
            }
        }
        finally { window.Close(); }
    }

    private static void ValidateScrollBar(ScrollBar bar, Orientation orientation)
    {
        bar.ApplyTemplate();
        Equal(orientation == Orientation.Vertical ? Application.Current.Resources["VerticalScrollBarTemplate"] : Application.Current.Resources["HorizontalScrollBarTemplate"], bar.Template, "Scrollbar uses the themed template");
        AssertDark(bar.Background, "Scrollbar track is dark");
        var track = (Track?)bar.Template.FindName("PART_Track", bar);
        Assert(track is not null, "Scrollbar exposes its standard Track part");
        Equal(orientation, track!.Orientation, "Track follows orientation");
        Equal(orientation == Orientation.Vertical ? ScrollBar.PageUpCommand : ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command, "Track page-up/left command preserved");
        Equal(orientation == Orientation.Vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command, "Track page-down/right command preserved");
        Assert(track.Thumb is not null, "Scrollbar remains draggable");
    }

    private static void AssertDark(Brush? brush, string message)
    {
        Assert(brush is SolidColorBrush, message + " (solid color expected)");
        var color = ((SolidColorBrush)brush!).Color;
        Assert(color.R < 130 && color.G < 130 && color.B < 130, message + $" ({color})");
    }

    private static void AssertPixelDark(BitmapSource bitmap, int x, int y, string message)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Assert(pixel[0] < 130 && pixel[1] < 130 && pixel[2] < 130, message + $" (BGR {pixel[0]},{pixel[1]},{pixel[2]})");
    }

    private static RenderTargetBitmap Capture(FrameworkElement element, string name, Brush background)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        Assert(width > 20 && height > 20, "Control has measurable dimensions: " + name);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backing = new DrawingVisual();
        using (var drawing = backing.RenderOpen()) drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
        bitmap.Render(backing);
        bitmap.Render(element);
        var destination = QaDirectory();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(destination, name))) encoder.Save(stream);
        Console.WriteLine("QA " + Path.Combine(destination, name));
        return bitmap;
    }

    private static string QaDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src", "MarkdownViewer", "MarkdownViewer.csproj")))
            repository = repository.Parent;
        Assert(repository is not null, "Repository located for QA artifacts");
        var destination = Path.Combine(repository!.FullName, "artifacts", "qa");
        Directory.CreateDirectory(destination);
        return destination;
    }

    private static async Task TestWindowRendering()
    {
        var sample = Fixture("README.md");
        await File.WriteAllTextAsync(Fixture("first.md"), "# Installation\n\nInstallation guide.");
        await File.WriteAllTextAsync(Fixture("second.md"), "# Changelog\n\nProject version history.");
        await File.WriteAllTextAsync(sample, """
            # Project documentation

            Everything you need to get started, in one place.

            > Keep your documentation next to the project, so it is always within reach.

            ## Getting started

            Start with the installation guide. The changelog lists updates between versions.

            ```csharp
            var files = Directory.GetFiles("docs", "*.md");
            foreach (var file in files)
                Console.WriteLine(file);
            ```

            ## Documents

            | File | Contents |
            | --- | --- |
            | README.md | Project overview |
            | Installation.md | Setup guide |
            | CHANGELOG.md | Version history |

            ## Checklist

            - [x] Open the documentation
            - [ ] Read the installation guide
            """);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src", "MarkdownViewer", "MarkdownViewer.csproj")))
            repository = repository.Parent;
        Assert(repository is not null, "Repository located for QA artifacts");
        var destination = Path.Combine(repository!.FullName, "artifacts", "qa");
        Directory.CreateDirectory(destination);

        foreach (var (theme, width, name) in new[] { ("Light", 1100, "window-light.png"), ("Dark", 1100, "window-dark.png"), ("Light", 640, "window-narrow.png") })
        {
            new SettingsStore().Save(new AppSettings { Theme = theme, OutlineVisible = true, WindowWidth = width, WindowHeight = 780 });
            var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = 0, Top = 0 };
            try
            {
                _ = new WindowInteropHelper(window).EnsureHandle();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await window.OpenFilesAsync([sample, Fixture("first.md"), Fixture("second.md"), sample]);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(width, 740));
                content.Arrange(new Rect(0, 0, width, 740));
                content.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                var status = (TextBlock)window.FindName("StatusText");
                status.Text = Path.GetFileName(status.Text);
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, 740, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen())
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, 740));
                bitmap.Render(background);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var output = Path.Combine(destination, name);
                using (var stream = File.Create(output)) encoder.Save(stream);
                Assert(new FileInfo(output).Length > 5_000, "Rendered window has visible content");
                Console.WriteLine("QA " + output);
            }
            finally { window.Close(); }
        }
    }

    private static IEnumerable<Block> Blocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            IEnumerable<Block> children = block switch
            {
                Section section => Blocks(section.Blocks),
                WpfList list => list.ListItems.SelectMany(item => Blocks(item.Blocks)),
                Table table => table.RowGroups.SelectMany(group => group.Rows).SelectMany(row => row.Cells).SelectMany(cell => Blocks(cell.Blocks)),
                _ => []
            };
            foreach (var child in children) yield return child;
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Equal<T>(T expected, T actual, string message)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected <{expected}>, actual <{actual}>"); }

}
