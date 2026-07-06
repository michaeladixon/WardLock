using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WardLock.ViewModels;

namespace WardLock.Views;

/// <summary>
/// Small popup shown near the cursor when the auto-type hotkey can't identify
/// a single account from the focused window's title. The user filters/picks an
/// account; the caller then types its code into the previously focused window.
/// </summary>
public partial class AutoTypePickerWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly List<AccountViewModel> _allAccounts;

    /// <summary>The account chosen by the user, or null if cancelled.</summary>
    public AccountViewModel? SelectedAccount { get; private set; }

    public AutoTypePickerWindow(IEnumerable<AccountViewModel> accounts)
    {
        InitializeComponent();
        _allAccounts = accounts.ToList();
        AccountList.ItemsSource = _allAccounts;
        if (_allAccounts.Count > 0)
            AccountList.SelectedIndex = 0;

        SourceInitialized += (_, _) => PositionAtCursor();
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void PositionAtCursor()
    {
        if (!GetCursorPos(out var pt)) return;

        // Convert device pixels to DIPs for the current DPI
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        var pos = transform?.Transform(new Point(pt.X, pt.Y)) ?? new Point(pt.X, pt.Y);

        // Keep the popup inside the work area
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, Math.Min(pos.X + 8, work.Right - Width));
        Top  = Math.Max(work.Top,  Math.Min(pos.Y + 8, work.Bottom - 300));
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var term = FilterBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(term)
            ? _allAccounts
            : _allAccounts.Where(a =>
                a.Issuer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                a.Label.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        AccountList.ItemsSource = filtered;
        if (filtered.Count > 0)
            AccountList.SelectedIndex = 0;
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        var count = AccountList.Items.Count;
        if (count == 0) return;

        if (e.Key == Key.Down)
        {
            AccountList.SelectedIndex = Math.Min(AccountList.SelectedIndex + 1, count - 1);
            AccountList.ScrollIntoView(AccountList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            AccountList.SelectedIndex = Math.Max(AccountList.SelectedIndex - 1, 0);
            AccountList.ScrollIntoView(AccountList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Clicking elsewhere cancels — never type into a window the user didn't pick from
        if (IsVisible && DialogResult == null)
            DialogResult = false;
    }

    private void Confirm()
    {
        if (AccountList.SelectedItem is not AccountViewModel vm) return;
        SelectedAccount = vm;
        DialogResult = true;
    }
}
