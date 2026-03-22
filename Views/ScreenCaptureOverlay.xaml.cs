using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WardLock.Views;

public partial class ScreenCaptureOverlay : Window
{
    private Point _startPoint;
    private bool _isSelecting;

    /// <summary>
    /// The selected screen region in screen coordinates, or null if cancelled.
    /// </summary>
    public Int32Rect? SelectedRegion { get; private set; }

    public ScreenCaptureOverlay()
    {
        InitializeComponent();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = null;
            Close();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = PointToScreen(e.GetPosition(this));
        _isSelecting = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, e.GetPosition(OverlayCanvas).X);
        Canvas.SetTop(SelectionRect, e.GetPosition(OverlayCanvas).Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
    }

    private void OnMouseMoveHandler(object sender, MouseEventArgs e)
    {
        if (!_isSelecting) return;

        var current = e.GetPosition(OverlayCanvas);
        var start = OverlayCanvas.PointFromScreen(_startPoint);

        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);
        var w = Math.Abs(current.X - start.X);
        var h = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        _isSelecting = false;

        var endPoint = PointToScreen(e.GetPosition(this));

        var x = (int)Math.Min(_startPoint.X, endPoint.X);
        var y = (int)Math.Min(_startPoint.Y, endPoint.Y);
        var w = (int)Math.Abs(endPoint.X - _startPoint.X);
        var h = (int)Math.Abs(endPoint.Y - _startPoint.Y);

        if (w > 10 && h > 10)
        {
            SelectedRegion = new Int32Rect(x, y, w, h);
        }
        else
        {
            SelectedRegion = null;
        }

        Close();
    }
}
