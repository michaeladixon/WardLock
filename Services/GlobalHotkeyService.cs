using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WardLock.Services;

/// <summary>
/// Registers a system-wide hotkey (default: Ctrl+Shift+A) to show/hide WardLock.
/// Uses Win32 RegisterHotKey — works even when the app is minimized to tray.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId = 0x9000;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_A = 0x41;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    public bool Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _registered = RegisterHotKey(_hwnd, HotkeyId, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, VK_A);
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
