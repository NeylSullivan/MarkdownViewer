using System.IO;
using System.Windows;
using MarkdownViewer.Services;

namespace MarkdownViewer;

public partial class App : Application
{
    private SingleInstanceService? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var files = e.Args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
            .Select(Path.GetFullPath).ToArray();
        _instance = new SingleInstanceService();
        if (!_instance.IsPrimary)
        {
            if (!await _instance.SendAsync(files))
                MessageBox.Show("Could not send the files to the running application. Please try again.", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        var startup = window.RestoreSessionAsync(files);
        _instance.StartListening(paths => Dispatcher.InvokeAsync(async () =>
        {
            await startup;
            if (Dispatcher.HasShutdownStarted || !window.IsLoaded) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            await window.OpenFilesAsync(paths);
        }));
        await startup;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
