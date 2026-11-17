using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace MarkdownViewer.UI;

public sealed class SmoothScrollController : IDisposable
{
    private const double Response = 22;
    private readonly RichTextBox _reader;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastFrame;
    private FlowDocument? _document;
    private double _position;
    private double _velocity;
    private int _direction;
    private bool _disposed;

    public SmoothScrollController(RichTextBox reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _timer = new DispatcherTimer(DispatcherPriority.Render, reader.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnFrame;
    }

    public bool IsAnimating => _timer.IsEnabled;
    public double TargetOffset { get; private set; }

    public void ScrollBy(double distance)
    {
        _reader.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(distance) || distance == 0) return;

        var direction = Math.Sign(distance);
        if (IsAnimating && (!ReferenceEquals(_document, _reader.Document) || direction != _direction))
            Cancel();

        if (!IsAnimating)
        {
            _document = _reader.Document;
            _position = _reader.VerticalOffset;
            TargetOffset = _position;
            _velocity = 0;
        }

        TargetOffset = Math.Clamp(TargetOffset + distance, 0, MaximumOffset);
        _direction = direction;
        if (Math.Abs(TargetOffset - _position) < 0.1)
        {
            Stop();
            return;
        }

        if (!IsAnimating)
        {
            _lastFrame = TimeSpan.Zero;
            _clock.Restart();
            _timer.Start();
        }
    }

    public void Cancel()
    {
        _reader.Dispatcher.VerifyAccess();
        var wasAnimating = IsAnimating;
        var sameDocument = ReferenceEquals(_document, _reader.Document);
        Stop();
        _position = _reader.VerticalOffset;
        TargetOffset = _position;
        if (wasAnimating && sameDocument)
        {
            // Replace any outstanding ScrollViewer command before another input
            // or document restore supplies its own destination.
            _reader.ScrollToVerticalOffset(_position);
            _reader.UpdateLayout();
        }
        _document = null;
    }

    internal void Advance(TimeSpan elapsed)
    {
        _reader.Dispatcher.VerifyAccess();
        if (!IsAnimating || elapsed <= TimeSpan.Zero) return;
        if (!ReferenceEquals(_document, _reader.Document))
        {
            Cancel();
            return;
        }

        var maximum = MaximumOffset;
        TargetOffset = Math.Clamp(TargetOffset, 0, maximum);
        _position = Math.Clamp(_position, 0, maximum);

        // The exact critically damped solution preserves velocity while a held
        // key extends the destination, regardless of the actual frame interval.
        var seconds = elapsed.TotalSeconds;
        var displacement = _position - TargetOffset;
        var adjustment = _velocity + Response * displacement;
        var decay = Math.Exp(-Response * seconds);
        _position = TargetOffset + (displacement + adjustment * seconds) * decay;
        _velocity = (_velocity - Response * adjustment * seconds) * decay;
        _position = Math.Clamp(_position, 0, maximum);

        var finished = Math.Abs(TargetOffset - _position) < 0.2 && Math.Abs(_velocity) < 4;
        if (finished) _position = TargetOffset;
        _reader.ScrollToVerticalOffset(_position);
        _reader.UpdateLayout();
        if (finished) Stop();
    }

    private double MaximumOffset
    {
        get
        {
            var maximum = _reader.ExtentHeight - _reader.ViewportHeight;
            return double.IsFinite(maximum) ? Math.Max(0, maximum) : 0;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var elapsed = now - _lastFrame;
        _lastFrame = now;
        Advance(elapsed);
    }

    private void Stop()
    {
        _timer.Stop();
        _clock.Reset();
        _velocity = 0;
        _direction = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel();
        _timer.Tick -= OnFrame;
        _disposed = true;
    }
}
