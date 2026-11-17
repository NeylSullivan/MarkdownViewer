using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MarkdownViewer.Services;

/// <summary>Forwards command-line files to the current user's existing window.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private const int MaximumMessageBytes = 128 * 1024;
    private readonly Mutex _instanceMutex;
    private readonly Thread _ownershipThread;
    private readonly ManualResetEventSlim _releaseOwnership = new();
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _disposed;

    public SingleInstanceService()
    {
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var user = currentIdentity.User?.Value
            ?? Environment.UserDomainName + "\\" + Environment.UserName;
        var settingsDirectory = Environment.GetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer");
        var identity = user + "|" + Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant()
            + "|" + Path.GetFullPath(settingsDirectory).ToUpperInvariant();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        _pipeName = "MarkdownViewer-" + suffix;

        _instanceMutex = new Mutex(false, "Local\\" + _pipeName);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Mutex ownership is thread-affine. Keep acquisition and release on this
        // thread so callers may dispose the service from any thread.
        _ownershipThread = new Thread(() =>
        {
            bool acquired;
            try
            {
                try { acquired = _instanceMutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            catch (Exception error) { ready.SetException(error); return; }
            ready.SetResult(acquired);
            if (!acquired) return;
            try { _releaseOwnership.Wait(); }
            finally { _instanceMutex.ReleaseMutex(); }
        }) { IsBackground = true, Name = "Markdown Viewer instance ownership" };
        _ownershipThread.Start();
        try { IsPrimary = ready.Task.GetAwaiter().GetResult(); }
        catch
        {
            _ownershipThread.Join();
            _instanceMutex.Dispose();
            _releaseOwnership.Dispose();
            _shutdown.Dispose();
            throw;
        }
    }

    public bool IsPrimary { get; }

    public async Task<bool> SendAsync(string[] paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(paths);
        if (IsPrimary)
            return false;

        byte[] payload;
        try
        {
            var fullPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
            payload = JsonSerializer.SerializeToUtf8Bytes(fullPaths);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (payload.Length > MaximumMessageBytes)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await client.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var response = new byte[1];
            await client.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    public void StartListening(Action<string[]> onFiles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onFiles);
        if (!IsPrimary)
            throw new InvalidOperationException("Only the primary application instance can listen.");
        if (_listener is not null)
            throw new InvalidOperationException("The instance listener is already running.");
        var token = _shutdown.Token;
        _listener = Task.Run(() => ListenAsync(onFiles, token), token);
    }

    private async Task ListenAsync(Action<string[]> onFiles, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[sizeof(int)];
                await server.ReadExactlyAsync(header, request.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 2 or > MaximumMessageBytes)
                    continue;

                var payload = new byte[length];
                await server.ReadExactlyAsync(payload, request.Token).ConfigureAwait(false);
                var paths = JsonSerializer.Deserialize<string[]>(payload);
                if (paths is null || paths.Any(path => path is null))
                    continue;
                onFiles(paths);
                await server.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                await server.FlushAsync(request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
            {
                // A disconnected or malformed request must not stop future activations.
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        try
        {
            // Close the old pipe before allowing another instance to listen.
            try { _listener?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            _releaseOwnership.Set();
            _ownershipThread.Join();
            _instanceMutex.Dispose();
            _releaseOwnership.Dispose();
            _shutdown.Dispose();
        }
    }
}
