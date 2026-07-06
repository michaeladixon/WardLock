using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WardLock.Services;

/// <summary>
/// Registers system-wide hotkeys via Win32 RegisterHotKey — they work even when
/// the app is minimized to tray:
///   Ctrl+Shift+A — show/hide WardLock
///   Ctrl+Shift+T — auto-type the current code into the focused window
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int ToggleHotkeyId   = 0x9000;
    private const int AutoTypeHotkeyId = 0x9001;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_A = 0x41;
    private const uint VK_T = 0x54;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _toggleRegistered;
    private bool _autoTypeRegistered;

    public event Action? HotkeyPressed;
    public event Action? AutoTypeHotkeyPressed;

    public bool Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _toggleRegistered   = RegisterHotKey(_hwnd, ToggleHotkeyId, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, VK_A);
        _autoTypeRegistered = RegisterHotKey(_hwnd, AutoTypeHotkeyId, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, VK_T);
        return _toggleRegistered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case ToggleHotkeyId:
                    HotkeyPressed?.Invoke();
                    handled = true;
                    break;
                case AutoTypeHotkeyId:
                    AutoTypeHotkeyPressed?.Invoke();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_toggleRegistered)
        {
            UnregisterHotKey(_hwnd, ToggleHotkeyId);
            _toggleRegistered = false;
        }
        if (_autoTypeRegistered)
        {
            UnregisterHotKey(_hwnd, AutoTypeHotkeyId);
            _autoTypeRegistered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
