using WardLock.Services;
using WardLock.Views;

namespace WardLock.ViewModels;

/// <summary>
/// One-keystroke code delivery (issue #1, Tier 1): Ctrl+Shift+T identifies the
/// matching account from the focused window's title and types the current code
/// into it. Ambiguous or no match falls back to a picker popup at the cursor.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Issuer/label fragments shorter than this never match a window title.</summary>
    private const int MinTitleMatchLength = 3;

    /// <summary>
    /// Entry point for the global auto-type hotkey. <paramref name="ownHwnd"/> is
    /// WardLock's main window handle, used to refuse typing into ourselves.
    /// </summary>
    public void AutoTypeIntoForegroundWindow(IntPtr ownHwnd)
    {
        var target = AutoTypeService.GetForegroundWindowHandle();
        if (target == IntPtr.Zero) return;

        if (target == ownHwnd)
        {
            StatusMessage = "Focus the window you want the code typed into, then press Ctrl+Shift+T.";
            return;
        }

        // Locked vault = no codes typed, ever. Surface the lock screen instead.
        if (!IsUnlocked)
        {
            RestoreWindow?.Invoke();
            StatusMessage = "Unlock WardLock first — codes are never typed while locked.";
            return;
        }

        if (Accounts.Count == 0)
        {
            RestoreWindow?.Invoke();
            StatusMessage = "No accounts yet. Add one before using auto-type.";
            return;
        }

        ResetIdleTimer();

        var title = AutoTypeService.GetWindowTitle(target);
        var matches = MatchAccountsByWindowTitle(title);

        AccountViewModel? account;
        if (matches.Count == 1)
        {
            // Confident match — type immediately, the target still holds focus
            account = matches[0];
        }
        else
        {
            // Ambiguous (or nothing matched): let the user pick, then restore focus
            var picker = new AutoTypePickerWindow(matches.Count > 0 ? matches : Accounts);
            if (picker.ShowDialog() != true || picker.SelectedAccount == null)
                return;

            account = picker.SelectedAccount;
            if (!AutoTypeService.RestoreFocus(target))
            {
                StatusMessage = "Couldn't refocus the target window. Code was not typed.";
                return;
            }
        }

        TypeCode(target, account);
    }

    private void TypeCode(IntPtr target, AccountViewModel account)
    {
        account.Refresh(); // make sure we type the current period's code
        var code = account.CurrentCode;
        if (string.IsNullOrEmpty(code))
        {
            StatusMessage = $"Couldn't generate a code for {account.DisplayName}.";
            return;
        }

        StatusMessage = AutoTypeService.TypeText(target, code)
            ? $"Typed code for {account.DisplayName}."
            : "Target window lost focus. Code was not typed.";
    }

    /// <summary>
    /// Accounts whose issuer or label appears in the window title (e.g. issuer
    /// "GitHub" matches "Sign in to GitHub — Firefox"). Case-insensitive substring
    /// on fragments of ≥3 chars to avoid noise matches.
    /// </summary>
    private List<AccountViewModel> MatchAccountsByWindowTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];

        return Accounts.Where(a =>
                FragmentMatches(title, a.Issuer) || FragmentMatches(title, a.Label))
            .ToList();
    }

    private static bool FragmentMatches(string title, string fragment)
        => fragment.Length >= MinTitleMatchLength
           && title.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
