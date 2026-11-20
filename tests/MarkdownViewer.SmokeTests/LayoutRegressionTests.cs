using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MarkdownViewer.Models;
using MarkdownViewer.Services;

internal static partial class Program
{
    private static async Task TestScrollPercentage()
    {
        var longPath = Fixture("scroll-progress-long.md");
        var shortPath = Fixture("scroll-progress-short.md");
        await File.WriteAllTextAsync(longPath, string.Join("\n\n", Enumerable.Range(1, 50).Select(i => $"Paragraph {i}.")));
        await File.WriteAllTextAsync(shortPath, "One paragraph.");
        var window = await PrepareWindow();
        try
        {
            var progress = (TextBlock)window.FindName("ScrollProgressText");
            var reader = (RichTextBox)window.FindName("Reader");
            var tabs = (ListBox)window.FindName("TabsList");
            Equal(string.Empty, progress.Text, "No percentage appears without an open document");

            await window.OpenFilesAsync([longPath]);
            await LayoutWindow(window);
            Equal("0%", progress.Text, "A long document opens at the top");
            reader.ScrollToVerticalOffset((reader.ExtentHeight - reader.ViewportHeight) / 2);
            await LayoutWindow(window);
            Equal("50%", progress.Text, "The midpoint is half of the scrollable height");
            await LayoutWindow(window, height: 480);
            var resizedPercent = 100 * reader.VerticalOffset / (reader.ExtentHeight - reader.ViewportHeight);
            Assert(resizedPercent < 49, "Resizing changes the scrollable range");
            Equal($"{resizedPercent:0}%", progress.Text, "Resizing refreshes the percentage without scrolling");

            await LayoutWindow(window);
            await window.OpenFilesAsync([shortPath]);
            await LayoutWindow(window);
            Equal("100%", progress.Text, "A whole document that fits is fully visible");
            tabs.SelectedIndex = 0;
            await LayoutWindow(window);
            Equal("50%", progress.Text, "Switching back restores the earlier percentage");
            reader.ScrollToEnd();
            await LayoutWindow(window);
            Equal("100%", progress.Text, "The last scroll position is 100 percent");
            while (window.Tabs.Count > 0) Invoke(window, "CloseTab", window.Tabs[0]);
            await LayoutWindow(window);
            Equal(string.Empty, progress.Text, "Closing all tabs clears the percentage");
        }
        finally { window.Close(); }
    }

    private static async Task TestWideOutlineNarrowWindow()
    {
        var store = new SettingsStore();
        store.Save(new AppSettings { OutlineVisible = true, OutlineWidth = 400, WindowWidth = 1100 });
        var path = Fixture("wide-outline.md");
        await File.WriteAllTextAsync(path, LongDocument("Wide outline"));
        var window = await PrepareWindow();
        double resizedWidth;
        try
        {
            await window.OpenFilesAsync([path]);
            await LayoutWindow(window);
            var content = (FrameworkElement)window.Content;
            var reader = (RichTextBox)window.FindName("Reader");
            var column = (ColumnDefinition)window.FindName("OutlineColumn");
            var toggle = (ToggleButton)window.FindName("OutlineToggle");

            void AssertReaderFits()
            {
                var bounds = reader.TransformToAncestor(content).TransformBounds(new Rect(reader.RenderSize));
                Assert(bounds.Left >= 0 && bounds.Right <= content.ActualWidth + 1,
                    $"Document fits the window: reader right {bounds.Right}, window width {content.ActualWidth}");
                var scrollbar = VisualDescendants<ScrollBar>(reader).Single(bar => bar.Orientation == Orientation.Vertical);
                Equal(Visibility.Visible, scrollbar.Visibility, "Long document retains its vertical scrollbar");
                var scrollbarBounds = scrollbar.TransformToAncestor(content).TransformBounds(new Rect(scrollbar.RenderSize));
                Assert(scrollbarBounds.Left >= 0 && scrollbarBounds.Right <= content.ActualWidth + 1,
                    "The entire vertical scrollbar remains inside the window");
            }

            Near(400, column.ActualWidth, 1, "Wide window uses the preferred outline width");
            window.Width = 640;
            await LayoutWindow(window, 640);
            Assert(column.ActualWidth < 400, "Narrow window reduces the outline width");
            AssertReaderFits();

            toggle.IsChecked = false;
            Invoke(window, "Outline_Click", toggle, new RoutedEventArgs());
            await LayoutWindow(window, 640);
            Near(0, column.ActualWidth, 1, "Hiding the outline releases its entire column");
            AssertReaderFits();
            toggle.IsChecked = true;
            Invoke(window, "Outline_Click", toggle, new RoutedEventArgs());
            await LayoutWindow(window, 640);
            AssertReaderFits();

            window.Width = 1100;
            await LayoutWindow(window);
            Near(400, column.ActualWidth, 1, "Expanding restores the preferred width after hide/show");
            AssertReaderFits();

            window.Width = 640;
            await LayoutWindow(window, 640);
            var beforeDrag = column.ActualWidth;
            var splitter = (GridSplitter)window.FindName("OutlineSplitter");
            splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            splitter.RaiseEvent(new DragDeltaEventArgs(-40, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            await LayoutWindow(window, 640);
            splitter.RaiseEvent(new DragCompletedEventArgs(-40, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await LayoutWindow(window, 640);
            resizedWidth = column.ActualWidth;
            Near(beforeDrag - 40, resizedWidth, 1, "Splitter still resizes an automatically constrained outline");
            AssertReaderFits();

            window.Width = 1100;
            await LayoutWindow(window);
            Near(resizedWidth, column.ActualWidth, 1, "A splitter drag becomes the new preferred width");
            AssertReaderFits();
        }
        finally { window.Close(); }
        Near(resizedWidth, store.Load().OutlineWidth, 1, "The explicit splitter width is saved");
    }
}
