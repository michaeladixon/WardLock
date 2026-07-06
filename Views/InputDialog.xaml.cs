using System.Windows;
using System.Windows.Input;

namespace WardLock.Views;

/// <summary>
/// Minimal single-value text prompt, styled like PasswordDialog.
/// </summary>
public partial class InputDialog : Window
{
    public string Value => ValueInput.Text;

    public InputDialog(string prompt, string initialValue = "")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        ValueInput.Text = initialValue;
        Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) DialogResult = false;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
