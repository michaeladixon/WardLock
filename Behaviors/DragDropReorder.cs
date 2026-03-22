using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WardLock.ViewModels;

namespace WardLock.Behaviors;

/// <summary>
/// Attached behavior that adds drag-and-drop reordering to a ListBox.
/// Usage: behaviors:DragDropReorder.IsEnabled="True"
/// </summary>
public static class DragDropReorder
{
    private static int _dragStartIndex = -1;
    private static Point _dragStartPoint;
    private static bool _isDragging;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(DragDropReorder),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static readonly DependencyProperty MoveCommandProperty =
        DependencyProperty.RegisterAttached("MoveCommand", typeof(ICommand), typeof(DragDropReorder));

    public static ICommand? GetMoveCommand(DependencyObject obj) => (ICommand?)obj.GetValue(MoveCommandProperty);
    public static void SetMoveCommand(DependencyObject obj, ICommand? value) => obj.SetValue(MoveCommandProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox) return;

        if ((bool)e.NewValue)
        {
            listBox.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            listBox.PreviewMouseMove += OnPreviewMouseMove;
            listBox.Drop += OnDrop;
            listBox.AllowDrop = true;
        }
        else
        {
            listBox.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            listBox.PreviewMouseMove -= OnPreviewMouseMove;
            listBox.Drop -= OnDrop;
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        _dragStartPoint = e.GetPosition(listBox);
        var item = GetListBoxItemAtPoint(listBox, _dragStartPoint);

        if (item != null)
        {
            _dragStartIndex = listBox.ItemContainerGenerator.IndexFromContainer(item);
        }
        else
        {
            _dragStartIndex = -1;
        }
        _isDragging = false;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStartIndex < 0) return;
        if (sender is not ListBox listBox) return;

        var pos = e.GetPosition(listBox);
        var diff = _dragStartPoint - pos;

        // Only start drag if moved enough (avoids accidental drags on click)
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (!_isDragging)
            {
                _isDragging = true;
                DragDrop.DoDragDrop(listBox, _dragStartIndex, DragDropEffects.Move);
                _isDragging = false;
                _dragStartIndex = -1;
            }
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (!e.Data.GetDataPresent(typeof(int))) return;

        var fromIndex = (int)e.Data.GetData(typeof(int))!;
        var pos = e.GetPosition(listBox);
        var targetItem = GetListBoxItemAtPoint(listBox, pos);

        int toIndex;
        if (targetItem != null)
        {
            toIndex = listBox.ItemContainerGenerator.IndexFromContainer(targetItem);
        }
        else
        {
            // Dropped below last item — move to end
            toIndex = listBox.Items.Count - 1;
        }

        if (fromIndex == toIndex || fromIndex < 0) return;

        var cmd = GetMoveCommand(listBox);
        if (cmd != null && cmd.CanExecute(null))
        {
            cmd.Execute(new MoveArgs(fromIndex, toIndex));
        }
    }

    private static ListBoxItem? GetListBoxItemAtPoint(ListBox listBox, Point point)
    {
        var element = listBox.InputHitTest(point) as DependencyObject;
        while (element != null)
        {
            if (element is ListBoxItem item) return item;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}

public record MoveArgs(int FromIndex, int ToIndex);
