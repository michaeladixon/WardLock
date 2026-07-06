using System.Runtime.InteropServices;
using System.Text;

namespace WardLock.Services;

/// <summary>
/// Win32 layer for one-keystroke code delivery (issue #1, Tier 1):
/// identifies the foreground window and types text into it via SendInput.
/// Contains no account/matching logic — that lives in MainViewModel.AutoType.
/// </summary>
public static class AutoTypeService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        // MOUSEINPUT (largest union member) is 32 bytes on x64; pad so
        // Marshal.SizeOf<INPUT>() matches what SendInput expects.
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Handle of the window the user is currently working in.</summary>
    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>Title bar text of a window, or empty string.</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Brings the target window back to the foreground (after our picker stole focus)
    /// and waits until it actually holds focus. Returns false if the window is gone
    /// or focus could not be restored — callers must not type in that case.
    /// </summary>
    public static bool RestoreFocus(IntPtr hwnd, int timeoutMs = 750)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (GetForegroundWindow() == hwnd) return true;

        SetForegroundWindow(hwnd);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    /// <summary>
    /// Types <paramref name="text"/> into the focused control of the foreground window.
    /// Uses KEYEVENTF_UNICODE so codes land correctly regardless of keyboard layout.
    /// Returns false if the expected window no longer holds focus (never types blind).
    /// </summary>
    public static bool TypeText(IntPtr expectedForeground, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (GetForegroundWindow() != expectedForeground) return false;

        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = MakeUnicodeKey(text[i], keyUp: false);
            inputs[i * 2 + 1] = MakeUnicodeKey(text[i], keyUp: true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static INPUT MakeUnicodeKey(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };
}
