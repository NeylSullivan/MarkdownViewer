using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownViewer.Models;
using MarkdownViewer.Rendering;
using MarkdownViewer.Services;
using MarkdownViewer.UI;
using Microsoft.Win32;

namespace MarkdownViewer;

public partial class MainWindow : Window
{
    private const double ZoomFontSizeStep = 16 * 0.1;

    private readonly SettingsStore _store = new();
    private readonly DocumentFileService _files = new();
    private readonly FileWatchService _watcher = new();
    private readonly MarkdownDocumentRenderer _renderer = new();
    private readonly DocumentSearchService _search = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<string, int> _reloadVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings;
    private readonly SmoothScrollController _smoothScroll;
    private readonly NativeTitleBar _titleBar;
    private readonly TabReorderController _tabReorder;
    private DocumentTab? _active;
    private IReadOnlyList<SearchMatch> _matches = [];
    private int _matchIndex = -1;
    private bool _dark;
    private bool _ready;
    private bool _restoringScroll;
    private int _displayVersion;
    private bool _restoringSession;
    private Task? _sessionRestoreTask;
    private bool _closing;

    public ObservableCollection<DocumentTab> Tabs { get; } = [];

    public MainWindow()
    {
        _settings = _store.Load();
        InitializeComponent();
        _titleBar = new NativeTitleBar(this, WindowContent, TitleBar, MaximizeButton);
        _tabReorder = new TabReorderController(TabsList, MoveTab);
        _smoothScroll = new SmoothScrollController(Reader);
        DataContext = this;
        RestoreWindowBounds();
        OutlineToggle.IsChecked = _settings.OutlineVisible;
        ApplyOutline();
        ApplyTheme();
        RefreshRecentFiles();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(true); };
        _watcher.FileChanged += (_, path) => Dispatcher.InvokeAsync(() => _ = ReloadFileAsync(path));
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        _ready = true;
        if (_store.LastError is { } error) ShowNotice(error);
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        try { await _openGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            await OpenFilesCoreAsync(paths);
            if (!_closing) { UpdateWatchers(); UpdateStatus(); }
        }
        finally { _openGate.Release(); }
    }

    public Task RestoreSessionAsync(IEnumerable<string> startupFiles) =>
        _sessionRestoreTask ??= RestoreSessionCoreAsync(startupFiles.ToArray());

    private async Task RestoreSessionCoreAsync(string[] startupFiles)
    {
        _restoringSession = true;
        var savedDocuments = _settings.OpenDocuments.ToArray();
        var savedActivePath = _settings.ActiveDocumentPath;
        var positions = savedDocuments.ToDictionary(entry => entry.FilePath, entry => entry.ScrollOffset, StringComparer.OrdinalIgnoreCase);
        var unavailable = new List<string>();
        try { await _openGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { _restoringSession = false; return; }
        try
        {
            await OpenFilesCoreAsync(savedDocuments.Select(entry => entry.FilePath), false, positions, unavailable);
            if (_closing) return;
            TabsList.SelectedItem = Tabs.FirstOrDefault(tab => string.Equals(tab.FilePath, savedActivePath, StringComparison.OrdinalIgnoreCase))
                ?? Tabs.FirstOrDefault();
            await OpenFilesCoreAsync(startupFiles, true, null, unavailable);
            if (_closing) return;
            UpdateWatchers();
            UpdateStatus();
            if (unavailable.Count > 0)
            {
                var names = string.Join(", ", unavailable.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).Select(Path.GetFileName));
                ShowNotice($"Could not reopen {unavailable.Count} file(s): {names}{(unavailable.Count > 3 ? ", …" : "")}. The available documents were restored.");
            }
        }
        finally
        {
            _restoringSession = false;
            _openGate.Release();
            if (!_closing) { CaptureSession(); ScheduleSave(); }
        }
    }

    private async Task OpenFilesCoreAsync(IEnumerable<string> paths, bool activate = true,
        IReadOnlyDictionary<string, double>? savedPositions = null, ICollection<string>? unavailable = null)
    {
        foreach (var path in paths)
        {
            if (_closing) break;
            try
            {
                var fullPath = Path.GetFullPath(path);
                var existing = Tabs.FirstOrDefault(tab => string.Equals(tab.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) { if (activate) TabsList.SelectedItem = existing; continue; }
                StatusText.Text = "Opening " + Path.GetFileName(fullPath) + "…";
                var document = await _files.LoadAsync(fullPath, _lifetime.Token);
                if (_closing) break;
                var tab = new DocumentTab(document.FullPath, document.Text, document.EncodingName)
                {
                    ScrollOffset = savedPositions?.GetValueOrDefault(document.FullPath)
                        ?? _settings.ScrollPositions.GetValueOrDefault(document.FullPath)
                };
                Tabs.Add(tab);
                if (activate)
                {
                    TabsList.SelectedItem = tab;
                    TabsList.ScrollIntoView(tab);
                    _store.AddRecent(_settings, fullPath);
                    RefreshRecentFiles();
                }
                ScheduleSave();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error) when (IsFileError(error))
            {
                if (unavailable is not null) unavailable.Add(path);
                else ShowNotice($"Could not open “{Path.GetFileName(path)}”. {error.Message}");
            }
        }
    }

    private async Task ReloadFileAsync(string path)
    {
        if (_closing) return;
        var tab = Tabs.FirstOrDefault(item => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (tab is null) return;
        var version = _reloadVersions.GetValueOrDefault(path) + 1;
        _reloadVersions[path] = version;
        try
        {
            var loaded = await _files.LoadAsync(path, _lifetime.Token);
            if (_closing || !Tabs.Contains(tab) || _reloadVersions.GetValueOrDefault(path) != version) return;
            if (loaded.Text == tab.Text && tab.Error is null) return;
            if (_active == tab) { _smoothScroll.Cancel(); RememberPosition(); }
            tab.Text = loaded.Text;
            tab.EncodingName = loaded.EncodingName;
            tab.Rendered = null;
            tab.Error = null;
            if (_active == tab) { DisplayDocument(tab); StatusText.Text = tab.FilePath + " · Updated"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsFileError(error))
        {
            if (!Tabs.Contains(tab)) return;
            tab.Error = "The file is unavailable. Showing the last loaded version. " + error.Message;
            if (_active == tab) ShowNotice(tab.Error);
        }
    }

    private static bool IsFileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != TabsList) return;
        _smoothScroll.Cancel();
        RememberPosition();
        _searchTimer.Stop();
        _search.Clear();
        _active = TabsList.SelectedItem as DocumentTab;
        if (_active is { } tab)
        {
            DisplayDocument(tab);
            TabsList.ScrollIntoView(tab);
        }
        else
        {
            _displayVersion++;
            _restoringScroll = false;
            Reader.Document = new FlowDocument();
            OutlineList.ItemsSource = null;
            WelcomePanel.Visibility = Visibility.Visible;
            SearchPanel.Visibility = Visibility.Collapsed;
            EmptyOutline.Visibility = Visibility.Collapsed;
            Title = "Markdown Viewer";
            _matches = [];
            UpdateStatus();
        }
        ScheduleSave();
    }

    private void DisplayDocument(DocumentTab tab)
    {
        _smoothScroll.Cancel();
        _search.Clear();
        _matches = [];
        _matchIndex = -1;
        _restoringScroll = true;
        var displayVersion = ++_displayVersion;
        var savedOffset = tab.ScrollOffset;
        tab.Rendered ??= _renderer.Render(tab.Text, tab.FilePath, _dark, _settings.FontSize, link => _ = NavigateAsync(tab, link));
        Reader.Document = tab.Rendered.Document;
        WelcomePanel.Visibility = Visibility.Collapsed;
        OutlineList.ItemsSource = tab.Rendered.Headings.Select(heading => new OutlineEntry(heading)).ToList();
        EmptyOutline.Visibility = tab.Rendered.Headings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = tab.FileName + " — Markdown Viewer";
        NoticePanel.Visibility = Visibility.Collapsed;
        if (tab.Error is { } error) ShowNotice(error);
        UpdateReadingWidth();
        UpdateStatus();
        if (SearchPanel.Visibility == Visibility.Visible) RunSearch(false);
        _ = RestoreScrollAsync(tab, displayVersion, savedOffset);
    }

    private async Task RestoreScrollAsync(DocumentTab tab, int displayVersion, double offset)
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (!IsCurrentDisplay()) return;
        Reader.UpdateLayout();
        Reader.ScrollToVerticalOffset(offset);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (!IsCurrentDisplay()) return;
        Reader.UpdateLayout();
        Reader.ScrollToVerticalOffset(offset);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (!IsCurrentDisplay()) return;
        _restoringScroll = false;
        RememberPosition();

        bool IsCurrentDisplay() => !_closing && _active == tab && displayVersion == _displayVersion;
    }

    private void CancelPendingScrollRestore()
    {
        if (!_restoringScroll) return;
        _displayVersion++;
        _restoringScroll = false;
    }

    private async Task NavigateAsync(DocumentTab source, string link)
    {
        try
        {
            if (link.StartsWith('#')) { JumpToAnchor(link[1..]); return; }
            if (Uri.TryCreate(link, UriKind.Absolute, out var absolute) && !absolute.IsFile)
            {
                if (absolute.Scheme is "https" or "http" or "mailto")
                    Process.Start(new ProcessStartInfo(absolute.AbsoluteUri) { UseShellExecute = true });
                else ShowNotice("This link type is not supported: " + absolute.Scheme);
                return;
            }
            var parts = link.Split('#', 2);
            var target = absolute?.IsFile == true ? absolute.LocalPath : Uri.UnescapeDataString(parts[0]);
            if (!Path.IsPathRooted(target)) target = Path.Combine(Path.GetDirectoryName(source.FilePath)!, target);
            var extension = Path.GetExtension(target).ToLowerInvariant();
            if (extension is ".md" or ".markdown" or ".mdown" or ".mkd" or ".txt")
            {
                await OpenFilesAsync([target]);
                var anchor = parts.Length > 1 ? parts[1] : absolute?.Fragment.TrimStart('#');
                if (!string.IsNullOrEmpty(anchor)) await Dispatcher.InvokeAsync(() => JumpToAnchor(anchor), DispatcherPriority.Loaded);
            }
            else ShowNotice("This viewer opens Markdown and text files. Link target: " + target);
        }
        catch (Exception error) when (IsFileError(error) || error is System.ComponentModel.Win32Exception or UriFormatException)
        { ShowNotice("Could not open the link. " + error.Message); }
    }

    private void JumpToAnchor(string anchor)
    {
        var decoded = Uri.UnescapeDataString(anchor);
        var heading = _active?.Rendered?.Headings.FirstOrDefault(item => string.Equals(item.Anchor, decoded, StringComparison.OrdinalIgnoreCase));
        if (heading is null) { ShowNotice("Section not found: " + decoded); return; }
        _smoothScroll.Cancel();
        CancelPendingScrollRestore();
        heading.Block.BringIntoView();
        OutlineList.SelectedItem = OutlineList.Items.OfType<OutlineEntry>().FirstOrDefault(entry => entry.Heading == heading);
    }

    private void CloseTab(DocumentTab? tab)
    {
        if (tab is null) return;
        if (_active == tab) { _smoothScroll.Cancel(); RememberPosition(); }
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        _reloadVersions.Remove(tab.FilePath);
        if (TabsList.SelectedItem is null && Tabs.Count > 0) TabsList.SelectedIndex = Math.Min(index, Tabs.Count - 1);
        UpdateWatchers();
        ScheduleSave();
    }

    private void UpdateWatchers()
    {
        try { _watcher.Watch(Tabs.Select(tab => tab.FilePath)); }
        catch (IOException error) { ShowNotice("Automatic refresh is temporarily unavailable. " + error.Message); }
        catch (UnauthorizedAccessException error) { ShowNotice("Automatic refresh is unavailable. " + error.Message); }
    }

    private void RememberPosition()
    {
        if (_active is null || _restoringScroll) return;
        _active.ScrollOffset = Reader.VerticalOffset;
        _store.RememberScrollPosition(_settings, _active.FilePath, _active.ScrollOffset);
    }

    private void Reader_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _restoringScroll || e.VerticalChange == 0) return;
        RememberPosition();
        ScheduleSave();
    }

    private void Outline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is OutlineEntry entry)
        {
            _smoothScroll.Cancel();
            CancelPendingScrollRestore();
            entry.Heading.Block.BringIntoView();
        }
    }

    private void ApplyOutline()
    {
        OutlinePanel.Visibility = _settings.OutlineVisible ? Visibility.Visible : Visibility.Collapsed;
        OutlineSplitter.Visibility = OutlinePanel.Visibility;
        OutlineColumn.MinWidth = _settings.OutlineVisible ? 150 : 0;
        OutlineColumn.Width = new GridLength(_settings.OutlineVisible ? _settings.OutlineWidth : 0);
        SplitterColumn.Width = new GridLength(_settings.OutlineVisible ? 4 : 0);
        UpdateOutlineWidth();
        if (_ready) UpdateReadingWidth();
    }

    private void UpdateOutlineWidth()
    {
        var availableWidth = WindowContent.ActualWidth > 0 ? WindowContent.ActualWidth : Width;
        OutlineColumn.MaxWidth = Math.Clamp(availableWidth - 300 - SplitterColumn.Width.Value, 150, 400);
    }

    private void Outline_Click(object sender, RoutedEventArgs e)
    {
        _smoothScroll.Cancel();
        _settings.OutlineVisible = OutlineToggle.IsChecked == true;
        ApplyOutline();
        ScheduleSave();
    }

    private void OutlineSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_settings.OutlineVisible) _settings.OutlineWidth = OutlineColumn.ActualWidth;
        ScheduleSave();
    }

    private void Reader_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (_ready) { _smoothScroll.Cancel(); UpdateReadingWidth(); } }

    private void UpdateReadingWidth()
    {
        if (_active is null || Reader.ActualWidth <= 0) return;
        var width = Math.Max(240, Reader.ActualWidth - 18);
        var padding = Math.Max(24, (width - _settings.ReadingWidth) / 2);
        Reader.Document.PagePadding = new Thickness(padding, 25, padding, 40);
        Reader.Document.PageWidth = width;
    }

    private void ChangeZoom(int direction)
    {
        if (direction == 0) return;
        var steps = Math.Round(_settings.FontSize / ZoomFontSizeStep, 8);
        var nextStep = direction > 0 ? Math.Floor(steps) + 1 : Math.Ceiling(steps) - 1;
        SetZoom(nextStep * ZoomFontSizeStep);
    }

    private void SetZoom(double fontSize)
    {
        _smoothScroll.Cancel();
        RememberPosition();
        _settings.FontSize = Math.Clamp(fontSize, 12, 28);
        InvalidateDocuments();
        ZoomButton.Content = Math.Round(_settings.FontSize / 16 * 100) + "%";
        ScheduleSave();
    }

    private void ApplyTheme()
    {
        _smoothScroll.Cancel();
        RememberPosition();
        _dark = ThemeManager.Apply(_settings.Theme, this);
        ZoomButton.Content = Math.Round(_settings.FontSize / 16 * 100) + "%";
        InvalidateDocuments();
    }

    private void InvalidateDocuments()
    {
        _search.Clear();
        foreach (var tab in Tabs) tab.Rendered = null;
        if (_active is { } active) DisplayDocument(active);
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == "System" && !_closing) Dispatcher.InvokeAsync(ApplyTheme);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var themeMenu = new MenuItem { Header = "Theme" };
        foreach (var theme in new[] { "System", "Light", "Dark" })
        {
            var item = new MenuItem { Header = theme, IsCheckable = true, IsChecked = _settings.Theme == theme };
            item.Click += (_, _) => { _settings.Theme = theme; ApplyTheme(); ScheduleSave(); };
            themeMenu.Items.Add(item);
        }
        menu.Items.Add(themeMenu);
        var widthMenu = new MenuItem { Header = "Reading width" };
        foreach (var (width, label) in new[] { (640d, "Narrow"), (860d, "Normal"), (1600d, "Wide") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Math.Abs(_settings.ReadingWidth - width) < 1 };
            item.Click += (_, _) => { _smoothScroll.Cancel(); _settings.ReadingWidth = width; UpdateReadingWidth(); ScheduleSave(); };
            widthMenu.Items.Add(item);
        }
        menu.Items.Add(widthMenu);
        var reload = new MenuItem { Header = "Refresh file    F5", IsEnabled = _active is not null };
        reload.Click += (_, _) => { if (_active is { } tab) _ = ReloadFileAsync(tab.FilePath); };
        menu.Items.Add(reload);
        var folder = new MenuItem { Header = "Show in File Explorer", IsEnabled = _active is not null };
        folder.Click += (_, _) =>
        {
            if (_active is { } tab) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + tab.FilePath + "\"") { UseShellExecute = true });
        };
        menu.Items.Add(folder);
        menu.Items.Add(new Separator());
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString();
        var about = new MenuItem { Header = "About" };
        about.Click += (_, _) => MessageBox.Show(this, $"Markdown Viewer {version}\n\nC# · WPF · Markdig\nA viewer for local Markdown files.\n\nCtrl+O — open files\nCtrl+F — show/hide search\nCtrl+Tab — next tab\nCtrl+W — close tab\nCtrl+Q — quit\nEsc — close search, then quit\nCtrl+mouse wheel — zoom\nCtrl+Shift+O — toggle outline\nUp / Down — scroll by line\nPage Up / Page Down — scroll by page\nSpace — scroll down one page\nHome / End — jump to document start / end\nF5 — refresh\n\nSettings: " + _store.FilePath, "About", MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(about);
        ShowMenu(menu, (Button)sender);
    }

    private void ShowMenu(ContextMenu menu, FrameworkElement target)
    {
        _smoothScroll.Cancel();
        target.ContextMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control))
            {
                menu.IsOpen = false;
                e.Handled = TryHandleShortcut(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement);
            }
        };
        menu.IsOpen = true;
    }

    private void RefreshRecentFiles() => WelcomeRecents.ItemsSource = _settings.RecentFiles.Take(5).Select(path => new { Name = Path.GetFileName(path), Path = path }).ToList();

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var path in _settings.RecentFiles.ToArray())
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path };
            item.Click += async (_, _) => await OpenFilesAsync([path]);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
        else
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear recent files" };
            clear.Click += (_, _) => { _settings.RecentFiles.Clear(); RefreshRecentFiles(); ScheduleSave(); };
            menu.Items.Add(clear);
        }
        ShowMenu(menu, RecentButton);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        _smoothScroll.Cancel();
        var dialog = new OpenFileDialog { Filter = "Markdown (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd|Text (*.txt)|*.txt|All files (*.*)|*.*", Multiselect = true, Title = "Open Markdown files" };
        if (dialog.ShowDialog(this) == true) await OpenFilesAsync(dialog.FileNames);
    }

    private async void WelcomeRecent_Click(object sender, RoutedEventArgs e)
    { if (((Button)sender).Tag is string path) await OpenFilesAsync([path]); }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    { CloseTab(((Button)sender).DataContext as DocumentTab); e.Handled = true; }

    private void Find_Click(object sender, RoutedEventArgs e) => ShowSearch();
    private void ShowSearch()
    {
        if (_active is null) return;
        _smoothScroll.Cancel();
        SearchPanel.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RunSearch(false);
    }

    private void CloseSearch()
    {
        _searchTimer.Stop();
        _search.Clear();
        _matches = [];
        SearchPanel.Visibility = Visibility.Collapsed;
        Reader.Focus();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    { if (!_ready) return; _searchTimer.Stop(); _searchTimer.Start(); }

    private void RunSearch(bool selectFirst)
    {
        if (_active is null || SearchPanel.Visibility != Visibility.Visible) return;
        _matches = _search.Find(Reader.Document, SearchBox.Text, _dark);
        _matchIndex = _matches.Count > 0 ? 0 : -1;
        UpdateSearchCount();
        if (selectFirst && _matches.Count > 0) SelectMatch();
    }

    private void MoveMatch(int direction)
    {
        if (_searchTimer.IsEnabled) { _searchTimer.Stop(); RunSearch(false); }
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + direction + _matches.Count) % _matches.Count;
        SelectMatch();
        UpdateSearchCount();
    }

    private void SelectMatch()
    {
        if (_matchIndex < 0 || _matchIndex >= _matches.Count) return;
        _smoothScroll.Cancel();
        CancelPendingScrollRestore();
        var match = _matches[_matchIndex];
        Reader.Selection.Select(match.Start, match.End);
        var rect = match.Start.GetCharacterRect(LogicalDirection.Forward);
        Reader.ScrollToVerticalOffset(Math.Max(0, Reader.VerticalOffset + rect.Top - Reader.ActualHeight / 3));
    }

    private void UpdateSearchCount()
    {
        SearchCount.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "Enter search text"
            : _matches.Count == 0 ? "No matches" : $"{_matchIndex + 1} of {_matches.Count}{(_search.Truncated ? "+" : "")}";
        SearchCount.ToolTip = _search.Truncated ? "More than 10,000 matches found. Refine your search."
            : _search.HighlightsLimited ? "The first 100 matches are highlighted. Use the navigation buttons to reach the remaining matches." : null;
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { MoveMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; } }
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => MoveMatch(-1);
    private void FindNext_Click(object sender, RoutedEventArgs e) => MoveMatch(1);
    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseSearch();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(16);

    private void Reader_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _smoothScroll.Cancel();
        CancelPendingScrollRestore();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ChangeZoom(Math.Sign(e.Delta)); e.Handled = true; }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _smoothScroll.Cancel();
        if (ReferenceEquals(e.OriginalSource, Reader)
            || (e.OriginalSource is System.Windows.Media.Visual visual && Reader.IsAncestorOf(visual)))
            CancelPendingScrollRestore();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleShortcut(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement)) e.Handled = true;
        else if (e.Key is Key.Home or Key.End)
        {
            _smoothScroll.Cancel();
            CancelPendingScrollRestore();
        }
    }

    private bool TryHandleShortcut(Key key, ModifierKeys modifiers, IInputElement? focusedElement)
    {
        if ((key == Key.Escape && modifiers == ModifierKeys.None)
            || (key == Key.Q && modifiers == ModifierKeys.Control))
        {
            if (key == Key.Escape && SearchPanel.Visibility == Visibility.Visible) CloseSearch();
            else Close();
            return true;
        }
        if (modifiers.HasFlag(ModifierKeys.Alt) || modifiers.HasFlag(ModifierKeys.Windows)) return false;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (key == Key.F5 && _active is { } active) { _ = ReloadFileAsync(active.FilePath); return true; }
        if (!ctrl)
        {
            if (_active is null || (focusedElement is TextBoxBase && !ReferenceEquals(focusedElement, Reader))
                || focusedElement is PasswordBox || OutlineList.IsKeyboardFocusWithin
                || (focusedElement is ButtonBase button && CaptionButtonsSpace.IsAncestorOf(button))
                || ReferenceEquals(focusedElement, OutlineList)) return false;
            if (!shift && key is Key.Home or Key.End)
            {
                _smoothScroll.Cancel();
                CancelPendingScrollRestore();
                if (key == Key.Home) Reader.ScrollToHome();
                else Reader.ScrollToEnd();
                return true;
            }
            var line = _settings.FontSize * 1.6;
            var page = Math.Max(line, Reader.ViewportHeight - line);
            var distance = key switch
            {
                Key.Up when !shift => -line,
                Key.Down when !shift => line,
                Key.PageUp when !shift => -page,
                Key.PageDown when !shift => page,
                Key.Space => shift ? -page : page,
                _ => 0
            };
            if (distance == 0) return false;
            CancelPendingScrollRestore();
            _smoothScroll.ScrollBy(distance);
            return true;
        }
        switch (key)
        {
            case Key.O when shift: OutlineToggle.IsChecked = OutlineToggle.IsChecked != true; Outline_Click(this, new RoutedEventArgs()); break;
            case Key.O: Open_Click(this, new RoutedEventArgs()); break;
            case Key.F:
                if (SearchPanel.Visibility == Visibility.Visible) CloseSearch();
                else ShowSearch();
                break;
            case Key.W: CloseTab(_active); break;
            case Key.Tab:
                if (Tabs.Count > 0) TabsList.SelectedIndex = (TabsList.SelectedIndex + (shift ? -1 : 1) + Tabs.Count) % Tabs.Count;
                break;
            case Key.Add: case Key.OemPlus: ChangeZoom(1); break;
            case Key.Subtract: case Key.OemMinus: ChangeZoom(-1); break;
            case Key.D0: case Key.NumPad0: SetZoom(16); break;
            default: return false;
        }
        return true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e)
    { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await OpenFilesAsync(paths); e.Handled = true; }

    private void ShowNotice(string message) { NoticeText.Text = message; NoticePanel.Visibility = Visibility.Visible; }
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => NoticePanel.Visibility = Visibility.Collapsed;
    private void UpdateStatus()
    { StatusText.Text = _active?.FilePath ?? "Open or drop a Markdown file"; StatusText.ToolTip = _active?.FilePath; EncodingText.Text = _active?.EncodingName ?? ""; }

    private void ScheduleSave()
    { if (!_ready || _closing || _restoringSession) return; _saveTimer.Stop(); _saveTimer.Start(); }
    private void SaveSettings()
    {
        CaptureSession();
        _store.Save(_settings);
        if (_store.LastError is { } error && !_closing) ShowNotice(error);
    }

    private void CaptureSession()
    {
        if (_restoringSession) return;
        RememberPosition();
        _settings.OpenDocuments = Tabs.Select(tab => new OpenDocumentState
        {
            FilePath = tab.FilePath,
            ScrollOffset = tab.ScrollOffset
        }).ToList();
        _settings.ActiveDocumentPath = _active?.FilePath;
    }

    private void RestoreWindowBounds()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var saved = new Rect(left, top, Width, Height);
            if (Rect.Intersect(area, saved) is { Width: > 160, Height: > 80 }) { Left = left; Top = top; WindowStartupLocation = WindowStartupLocation.Manual; }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyTheme();
    private void WindowContent_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (_ready && e.WidthChanged) UpdateOutlineWidth(); }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (_ready) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateReadingWidth); }

    private void Tabs_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_closing && TabsList.SelectedItem is { } tab) TabsList.ScrollIntoView(tab);
            });
    }

    private void MoveTab(DocumentTab tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (_closing || oldIndex < 0 || newIndex < 0 || newIndex >= Tabs.Count || oldIndex == newIndex) return;
        Tabs.Move(oldIndex, newIndex);
        TabsList.ScrollIntoView(tab);
        ScheduleSave();
    }

    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabsList.Template.FindName("PART_ScrollViewer", TabsList) is not ScrollViewer viewer) return;
        if (e.Delta > 0) viewer.LineLeft();
        else if (e.Delta < 0) viewer.LineRight();
        e.Handled = true;
    }

    private void TabsScrollLeft_Click(object sender, RoutedEventArgs e) =>
        (TabsList.Template.FindName("PART_ScrollViewer", TabsList) as ScrollViewer)?.LineLeft();

    private void TabsScrollRight_Click(object sender, RoutedEventArgs e) =>
        (TabsList.Template.FindName("PART_ScrollViewer", TabsList) as ScrollViewer)?.LineRight();

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _tabReorder.Dispose();
        _titleBar.Dispose();
        _smoothScroll.Dispose();
        RememberPosition();
        _closing = true;
        _lifetime.Cancel();
        _watcher.Dispose();
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        _saveTimer.Stop();
        _searchTimer.Stop();
        _search.Clear();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.Maximized = WindowState == WindowState.Maximized;
        SaveSettings();
    }

    private sealed record OutlineEntry(DocumentHeading Heading)
    {
        public string Title => Heading.Title;
        public Thickness Indent => new(Math.Max(0, Heading.Level - 1) * 10, 0, 0, 0);
    }
}
