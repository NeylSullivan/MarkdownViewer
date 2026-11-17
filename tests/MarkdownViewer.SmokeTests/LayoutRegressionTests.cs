using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MarkdownViewer.Models;
using MarkdownViewer.Services;

internal static partial class Program
{
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
