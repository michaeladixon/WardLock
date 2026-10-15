using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace WardLock.Services;

/// <summary>
/// Clipboard writes for one-time codes: kept out of Windows clipboard history
/// and Cloud Clipboard sync, and cleared after a short time if nothing else
/// has been copied since.
/// </summary>
public static class SecureClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    // Registered formats Windows checks before adding an item to clipboard
    // history / cloud sync (learn.microsoft.com/windows/win32/dataxchg/clipboard-formats).
    private const string ExcludeFromMonitors = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private static DispatcherTimer? _clearTimer;
    private static uint _ourSequence;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// Copy <paramref name="text"/>. Returns false when another process holds
    /// the clipboard open (CLIPBRD_E_CANT_OPEN after WPF's retries).
    /// </summary>
    public static bool TrySetText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData(ExcludeFromMonitors, new MemoryStream(new byte[4]));
        data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)));

        try
        {
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (ExternalException)
        {
            return false;
        }

        _ourSequence = GetClipboardSequenceNumber();
        _clearTimer ??= CreateClearTimer();
        _clearTimer.Stop();
        _clearTimer.Start();
        return true;
    }

    /// <summary>
    /// Clear the clipboard if it still holds what we put there (nothing has been
    /// copied since, per the clipboard sequence number). Called on timeout and lock.
    /// </summary>
    public static void ClearIfStillOurs()
    {
        _clearTimer?.Stop();
        if (_ourSequence == 0) return;
        try
        {
            if (GetClipboardSequenceNumber() == _ourSequence)
                Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // Clipboard busy — leave it; the code expires on its own.
        }
        _ourSequence = 0;
    }

    /// <summary>Clear the clipboard if its text is exactly <paramref name="text"/> (e.g. a pasted otpauth:// URI).</summary>
    public static void ClearIfText(string text)
    {
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText().Trim() == text.Trim())
                Clipboard.Clear();
        }
        catch (ExternalException)
        {
        }
    }

    private static DispatcherTimer CreateClearTimer()
    {
        var timer = new DispatcherTimer { Interval = ClearAfter };
        timer.Tick += (_, _) => ClearIfStillOurs();
        return timer;
    }
}
