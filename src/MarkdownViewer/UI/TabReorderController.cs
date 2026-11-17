using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MarkdownViewer.UI;

internal sealed class TabReorderController : IDisposable
{
    private readonly ListBox _tabs;
    private readonly Action<DocumentTab, int> _moveTab;
    private readonly Window? _window;
    private readonly DispatcherTimer _scrollTimer;
    private DocumentTab? _candidate;
    private ListBoxItem? _draggedItem;
    private InsertionAdorner? _indicator;
    private AdornerLayer? _adornerLayer;
    private Point _start;
    private bool _startingDrag;
    private bool _dragging;
    private int _destination = -1;
    private int _scrollDirection;

    public TabReorderController(ListBox tabs, Action<DocumentTab, int> moveTab)
    {
        _tabs = tabs;
        _moveTab = moveTab;
        _window = Window.GetWindow(tabs);
        _scrollTimer = new DispatcherTimer(DispatcherPriority.Input, tabs.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _scrollTimer.Tick += ScrollTimer_Tick;
        tabs.PreviewMouseLeftButtonDown += MouseDown;
        tabs.PreviewMouseMove += MouseMove;
        tabs.PreviewMouseLeftButtonUp += MouseUp;
        tabs.LostMouseCapture += LostCapture;
        tabs.Unloaded += Unloaded;
        ((INotifyCollectionChanged)tabs.Items).CollectionChanged += ItemsChanged;
        if (_window is not null) _window.Deactivated += Deactivated;
    }

    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        CancelDrag();
        if (_tabs.Items.Count < 2) return;
        _candidate = GetTabAt(e.OriginalSource as DependencyObject);
        _start = e.GetPosition(_tabs);
        // Let ListBox select the tab normally until the pointer crosses the drag threshold.
    }

    private DocumentTab? GetTabAt(DependencyObject? source)
    {
        if (source is not Visual) return null;
        for (var element = source; element is not null && element != _tabs; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase) return null;
            if (element is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == _tabs)
                return item.DataContext as DocumentTab;
        }
        return null;
    }

    private void MouseMove(object sender, MouseEventArgs e)
    {
        if (_startingDrag) { e.Handled = true; return; }
        if (_candidate is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
        var position = e.GetPosition(_tabs);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!BeginDrag(_candidate)) return;
            _tabs.SelectedItem = _candidate;
        }
        UpdateDrag(position);
        e.Handled = true;
    }

    private bool BeginDrag(DocumentTab tab)
    {
        if (_startingDrag || _dragging) return _dragging && _candidate == tab;
        if (_tabs.Items.Count < 2 || _tabs.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item)
            return false;
        _candidate = tab;
        _draggedItem = item;
        _dragging = true;
        _startingDrag = true;
        try
        {
            // CaptureMouse synchronously raises mouse events. Claim the drag first so
            // reentrant input cannot create another indicator or update partial state.
            // Capturing the item also stops ListBox's own selection/autoscroll timer.
            if (!item.CaptureMouse() || !_dragging || Mouse.Captured != item)
            {
                CancelDrag();
                return false;
            }
            item.SetCurrentValue(UIElement.OpacityProperty, 0.55);
            item.SetCurrentValue(FrameworkElement.CursorProperty, Cursors.Hand);
            _adornerLayer = AdornerLayer.GetAdornerLayer(_tabs);
            if (_adornerLayer is not null)
            {
                _indicator = new InsertionAdorner(_tabs) { IsHitTestVisible = false };
                _adornerLayer.Add(_indicator);
            }
            return true;
        }
        finally { _startingDrag = false; }
    }

    private void UpdateDrag(Point position)
    {
        if (!_dragging) return;
        _destination = -1;
        if (!new Rect(_tabs.RenderSize).Contains(position))
        {
            if (_indicator is not null) _indicator.Visibility = Visibility.Hidden;
            _scrollTimer.Stop();
            return;
        }

        var viewer = ScrollViewer;
        var viewport = viewer is null ? new Rect(_tabs.RenderSize)
            : viewer.TransformToAncestor(_tabs).TransformBounds(new Rect(viewer.RenderSize));
        var x = Math.Clamp(position.X, viewport.Left, viewport.Right);
        var insertion = _tabs.Items.Count;
        var marker = viewport.Right;
        for (var index = 0; index < _tabs.Items.Count; index++)
        {
            if (_tabs.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) continue;
            var bounds = item.TransformToAncestor(_tabs).TransformBounds(new Rect(item.RenderSize));
            if (x < bounds.Left + bounds.Width / 2)
            {
                insertion = index;
                marker = bounds.Left;
                break;
            }
            marker = bounds.Right;
        }
        var previous = _tabs.Items.IndexOf(_candidate);
        _destination = insertion > previous ? insertion - 1 : insertion;
        if (_indicator is not null)
        {
            _indicator.Position = Math.Clamp(marker, viewport.Left, viewport.Right);
            _indicator.Visibility = Visibility.Visible;
            _indicator.InvalidateVisual();
        }

        _scrollDirection = viewer is not null && position.X < viewport.Left + 24 && viewer.HorizontalOffset > 0 ? -1
            : viewer is not null && position.X > viewport.Right - 24 && viewer.HorizontalOffset < viewer.ScrollableWidth ? 1 : 0;
        if (_scrollDirection == 0) _scrollTimer.Stop();
        else _scrollTimer.Start();
    }

    private ScrollViewer? ScrollViewer => _tabs.Template.FindName("PART_ScrollViewer", _tabs) as ScrollViewer;

    private void ScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_scrollDirection < 0) ScrollViewer?.LineLeft();
        else if (_scrollDirection > 0) ScrollViewer?.LineRight();
        _tabs.UpdateLayout();
        UpdateDrag(Mouse.GetPosition(_tabs));
    }

    private void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) { _candidate = null; return; }
        CompleteDrag(e.GetPosition(_tabs));
        e.Handled = true;
    }

    private void CompleteDrag(Point position)
    {
        UpdateDrag(position);
        var tab = _candidate;
        var destination = _destination;
        CancelDrag();
        if (tab is not null && destination >= 0) _moveTab(tab, destination);
    }

    private void CancelDrag()
    {
        var item = _draggedItem;
        _dragging = false;
        _candidate = null;
        _draggedItem = null;
        _destination = -1;
        _scrollDirection = 0;
        _scrollTimer.Stop();
        if (_indicator is not null) _adornerLayer?.Remove(_indicator);
        _indicator = null;
        _adornerLayer = null;
        if (item is not null)
        {
            item.ClearValue(UIElement.OpacityProperty);
            item.ClearValue(FrameworkElement.CursorProperty);
            if (Mouse.Captured == item) item.ReleaseMouseCapture();
        }
    }

    private void LostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging && e.OriginalSource == _draggedItem && Mouse.Captured != _draggedItem) CancelDrag();
    }

    private void Deactivated(object? sender, EventArgs e) => CancelDrag();
    private void Unloaded(object sender, RoutedEventArgs e) => CancelDrag();
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => CancelDrag();

    public void Dispose()
    {
        CancelDrag();
        _scrollTimer.Tick -= ScrollTimer_Tick;
        _tabs.PreviewMouseLeftButtonDown -= MouseDown;
        _tabs.PreviewMouseMove -= MouseMove;
        _tabs.PreviewMouseLeftButtonUp -= MouseUp;
        _tabs.LostMouseCapture -= LostCapture;
        _tabs.Unloaded -= Unloaded;
        ((INotifyCollectionChanged)_tabs.Items).CollectionChanged -= ItemsChanged;
        if (_window is not null) _window.Deactivated -= Deactivated;
    }

    private sealed class InsertionAdorner(ListBox tabs) : Adorner(tabs)
    {
        public double Position { get; set; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var pen = new Pen((Brush)tabs.FindResource("AccentBrush"), 3);
            drawingContext.DrawLine(pen, new Point(Position, 2), new Point(Position, ActualHeight - 2));
        }
    }
}
