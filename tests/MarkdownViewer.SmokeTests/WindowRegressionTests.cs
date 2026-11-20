using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MarkdownViewer;
using MarkdownViewer.Models;
using MarkdownViewer.Services;

internal static partial class Program
{
    private static async Task TestDocumentStatus()
    {
        new SettingsStore().Save(new AppSettings());
        var markdown = Fixture("status.md");
        var empty = Fixture("status-empty.md");
        await File.WriteAllTextAsync(markdown, "# Hello", Encoding.Unicode);
        await File.WriteAllTextAsync(empty, "");
        var window = await PrepareWindow();
        try
        {
            var tokens = (TextBlock)window.FindName("TokenCountText");
            var status = (TextBlock)window.FindName("StatusText");
            Equal("", tokens.Text, "No token count before a document is opened");
            await window.OpenFilesAsync([markdown]);
            await WaitUntil(() => tokens.Text == "2 tokens", "Markdown heading marker counts while the UTF-16 BOM is excluded");
            await window.OpenFilesAsync([empty]);
            await WaitUntil(() => tokens.Text == "0 tokens", "Empty file has a zero count");

            const string updated = "お誕生日おめでとう";
            await File.WriteAllTextAsync(markdown, updated);
            await WaitUntil(() => window.Tabs[0].Text == updated, "Background tab receives the external edit", 5000);
            Equal("0 tokens", tokens.Text, "Background reload preserves the selected file's count");
            Equal(empty, status.Text, "Background reload preserves the selected file's status");

            var tabs = (ListBox)window.FindName("TabsList");
            tabs.SelectedIndex = 0;
            Assert(status.Text.StartsWith("Updated externally at ", StringComparison.Ordinal), "Returning to an externally updated file shows the update time");
            await WaitUntil(() => tokens.Text == "8 tokens", "Updated text is counted with the o200k_base vocabulary");
            Assert(Shortcut(window, Key.F5, ModifierKeys.None, window), "F5 triggers a manual refresh");
            await WaitUntil(() => status.Text.StartsWith("Refreshed at ", StringComparison.Ordinal), "Manual refresh is reported separately from external changes");

            await File.WriteAllTextAsync(markdown, "hello");
            await WaitUntil(() => tokens.Text == "1 token", "Visible file's count changes after an external edit", 5000);
            Assert(status.Text.StartsWith("Updated externally at ", StringComparison.Ordinal), "Active tab reports the new external update");
            Invoke(window, "CloseTab", window.Tabs[0]);
            await WaitUntil(() => tokens.Text == "0 tokens", "Closing an active tab restores the other tab's count");
            Invoke(window, "CloseTab", window.Tabs[0]);
            Equal("", tokens.Text, "Closing the last tab clears the token count");
        }
        finally { window.Close(); }
    }

    private static async Task TestInvalidDocuments()
    {
        var binary = Fixture("invalid-binary.md");
        var oversized = Fixture("invalid-oversized.md");
        var valid = Fixture("invalid-test-valid.md");
        await File.WriteAllBytesAsync(binary, [65, 0, 66]);
        using (var stream = File.Create(oversized)) stream.SetLength(DocumentFileService.MaximumFileSize + 1);
        const string original = "# Valid document\n\nLast successfully loaded text.";
        await File.WriteAllTextAsync(valid, original);
        new SettingsStore().Save(new AppSettings());
        var window = await PrepareWindow();
        try
        {
            await window.OpenFilesAsync([valid]);
            await LayoutWindow(window);
            var reader = (RichTextBox)window.FindName("Reader");
            var notice = (FrameworkElement)window.FindName("NoticePanel");
            var noticeText = (TextBlock)window.FindName("NoticeText");
            foreach (var (path, message) in new[] { (binary, "binary data"), (oversized, "20 MiB") })
            {
                await window.OpenFilesAsync([path]);
                Equal(1, window.Tabs.Count, "Invalid input does not create a tab");
                Equal(Visibility.Visible, notice.Visibility, "Invalid input produces a visible notice");
                Assert(noticeText.Text.Contains(message), "Notice explains why the file is rejected");
                Assert(DocumentText(reader.Document).Contains("Last successfully loaded text."), "Invalid input preserves the active document");

                File.Copy(path, valid, overwrite: true);
                await (Task)Invoke(window, "ReloadFileAsync", valid)!;
                Equal(original, window.Tabs[0].Text, "Failed reload preserves the last valid document");
                Assert(window.Tabs[0].Error?.Contains(message) == true, "Failed reload records the validation error");
                Equal(Visibility.Visible, notice.Visibility, "Failed reload displays a notice");
                await File.WriteAllTextAsync(valid, original);
                await (Task)Invoke(window, "ReloadFileAsync", valid)!;
                Assert(window.Tabs[0].Error is null, "Reload recovers when the content becomes valid again");
            }

            Invoke(window, "CloseTab", window.Tabs[0]);
            await window.OpenFilesAsync([binary, oversized, valid]);
            Equal(1, window.Tabs.Count, "Batch opening continues past invalid inputs");
            Equal(valid, window.Tabs[0].FilePath, "Valid file after rejected files is opened");
        }
        finally { window.Close(); }

        new SettingsStore().Save(new AppSettings
        {
            OpenDocuments = [new() { FilePath = binary }, new() { FilePath = oversized }, new() { FilePath = valid }],
            ActiveDocumentPath = binary
        });
        var restored = await PrepareWindow();
        try
        {
            await restored.RestoreSessionAsync([]);
            Equal(1, restored.Tabs.Count, "Session restore skips both invalid documents");
            Equal(valid, restored.Tabs[0].FilePath, "Session still restores the valid document");
            Assert(((TextBlock)restored.FindName("NoticeText")).Text.Contains("Could not reopen 2 file(s)"), "Session restore reports rejected documents");
        }
        finally { restored.Close(); }
    }

    private static async Task TestSearchAfterInterruptedRestore()
    {
        new SettingsStore().Save(new AppSettings());
        var first = Fixture("search-interrupt-first.md");
        var second = Fixture("search-interrupt-second.md");
        await File.WriteAllTextAsync(first, "# First\n\nneedle\n\n" + string.Join("\n\n", Enumerable.Repeat("Paragraph for scrolling.", 80)) + "\n\nneedle needle");
        await File.WriteAllTextAsync(second, "# Second\n\nneedle");
        var window = await PrepareWindow();
        try
        {
            await window.OpenFilesAsync([first, second]);
            await LayoutWindow(window);
            var reader = (RichTextBox)window.FindName("Reader");
            var tabs = (ListBox)window.FindName("TabsList");
            var count = (TextBlock)window.FindName("SearchCount");
            Invoke(window, "ShowSearch");
            ((TextBox)window.FindName("SearchBox")).Text = "needle";
            await WaitUntil(() => count.Text == "1 of 1", "Initial search completes");

            tabs.SelectedIndex = 0;
            reader.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent
            });
            await LayoutWindow(window);
            Equal("1 of 3", count.Text, "Search count reflects the new document after interrupted scroll restore");
            Invoke(window, "MoveMatch", 1);
            Equal("2 of 3", count.Text, "Next match still works after immediately scrolling");
            Equal("needle", reader.Selection.Text, "Search selects text in the current document");

            tabs.SelectedIndex = 1;
            Assert(Shortcut(window, Key.Down, ModifierKeys.None, reader), "Reading shortcut interrupts the next restore");
            await LayoutWindow(window);
            Equal("1 of 1", count.Text, "Keyboard interruption also preserves search results");
            Invoke(window, "MoveMatch", 1);
            Equal("needle", reader.Selection.Text, "Search navigation works in the second document");
        }
        finally { window.Close(); }
    }
}
