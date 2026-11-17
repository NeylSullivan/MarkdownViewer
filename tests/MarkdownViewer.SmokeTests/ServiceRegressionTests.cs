using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using MarkdownViewer.Services;

internal static partial class Program
{
    private static bool TryHandleServiceRegressionChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.FirstOrDefault() != "--instance-owner-child") return false;
        using var instance = new SingleInstanceService();
        if (!instance.IsPrimary) { exitCode = 3; return true; }
        instance.StartListening(_ => { });
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return true;
    }

    private static async Task TestWatcherRecovery()
    {
        var directory = Fixture("watcher-recovery");
        var path = Path.Combine(directory, "document.md");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "initial");
        var changes = Channel.CreateUnbounded<string>();
        using var watcher = new FileWatchService();
        watcher.FileChanged += (_, changed) => changes.Writer.TryWrite(changed);
        watcher.Watch([path]);

        async Task ExpectChange(string message)
        {
            var changed = await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Equal(path, changed, message);
        }

        void DiscardChanges()
        {
            while (changes.Reader.TryRead(out _)) { }
        }

        await File.WriteAllTextAsync(path, "first edit");
        await ExpectChange("Initial watcher reports edits");
        File.Delete(path);
        Directory.Delete(directory);
        await ExpectChange("Removing the document reports its disappearance");
        await Task.Delay(1200);
        DiscardChanges();

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "recreated");
        await ExpectChange("Recreated directory reconnects without another Watch call");
        DiscardChanges();
        await File.WriteAllTextAsync(path, "second edit");
        await ExpectChange("Recovered watcher reports subsequent edits");

        File.Delete(path);
        Directory.Delete(directory);
        await ExpectChange("Second disappearance is reported");
        watcher.Watch([]);
        DiscardChanges();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "unwatched");
        await Task.Delay(1000);
        Assert(!changes.Reader.TryRead(out _), "Unwatch cancels recovery and pending notifications");

        watcher.Watch([path]);
        await File.WriteAllTextAsync(path, "watched again");
        await ExpectChange("Watching can resume after cancellation");
        File.Delete(path);
        Directory.Delete(directory);
        await ExpectChange("Disappearance before disposal is reported");
        watcher.Dispose();
        DiscardChanges();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "after disposal");
        await Task.Delay(1000);
        Assert(!changes.Reader.TryRead(out _), "Dispose cancels recovery and pending notifications");
    }

    private static async Task TestSingleInstanceRecovery()
    {
        var previousDirectory = Environment.GetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR");
        Environment.SetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR", Fixture("instance-recovery-settings"));
        try
        {
            var path = Fixture("instance-recovery.md");
            using (var primary = new SingleInstanceService())
            using (var secondary = new SingleInstanceService())
            {
                Assert(primary.IsPrimary && !secondary.IsPrimary, "Exactly one instance owns the mutex");
                var initial = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                primary.StartListening(paths => initial.TrySetResult(paths));
                Assert(await secondary.SendAsync([path]), "The initial primary receives files");
                Equal(path, (await initial.Task.WaitAsync(TimeSpan.FromSeconds(5))).Single(), "Initial payload is intact");

                await Task.Run(primary.Dispose);
                using var replacement = new SingleInstanceService();
                Assert(replacement.IsPrimary, "Cross-thread disposal releases ownership while a secondary remains alive");
                var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                replacement.StartListening(paths => received.TrySetResult(paths));
                Assert(await secondary.SendAsync([path]), "Existing secondary forwards to the replacement primary");
                Equal(path, (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Single(), "Replacement payload is intact");
            }

            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--instance-owner-child");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start primary fixture");
            try
            {
                Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)), "Child primary is ready");
                using var secondary = new SingleInstanceService();
                Assert(!secondary.IsPrimary, "Child holds primary ownership");
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

                using var replacement = new SingleInstanceService();
                Assert(replacement.IsPrimary, "Abandoned mutex can be acquired while the secondary remains alive");
                var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                replacement.StartListening(paths => received.TrySetResult(paths));
                Assert(await secondary.SendAsync([path]), "Files reach the new primary after a process crash");
                Equal(path, (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Single(), "Crash-recovery payload is intact");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR", previousDirectory); }
    }
}
