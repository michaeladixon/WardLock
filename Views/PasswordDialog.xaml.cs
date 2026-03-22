using System.Windows;
using System.Windows.Input;

namespace WardLock.Views;

public partial class PasswordDialog : Window
{
    public string? Password { get; private set; }
    private readonly bool _requireConfirmation;

    /// <param name="prompt">Text shown above the password field.</param>
    /// <param name="requireConfirmation">If true, shows a confirm field (for export). False for import.</param>
    public PasswordDialog(string prompt, bool requireConfirmation = true)
    {
        InitializeComponent();
        _requireConfirmation = requireConfirmation;
        PromptText.Text = prompt;
        ConfirmPanel.Visibility = requireConfirmation ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryAccept();
        if (e.Key == Key.Escape) { Password = null; DialogResult = false; }
    }

    private void OnOk(object sender, RoutedEventArgs e) => TryAccept();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Password = null;
        DialogResult = false;
    }

    private void TryAccept()
    {
        var pw = PasswordInput.Password;
        if (string.IsNullOrEmpty(pw))
        {
            ErrorText.Text = "Password is required.";
            return;
        }
        if (pw.Length < 8)
        {
            ErrorText.Text = "Password must be at least 8 characters.";
            return;
        }
        if (_requireConfirmation && pw != ConfirmInput.Password)
        {
            ErrorText.Text = "Passwords do not match.";
            return;
        }
        Password = pw;
        DialogResult = true;
    }
}
