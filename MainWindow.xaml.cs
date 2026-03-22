using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WardLock.Services;
using WardLock.ViewModels;

namespace WardLock;

public partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkey = new();
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Register global hotkey (Ctrl+Shift+A)
        if (_hotkey.Register(this))
        {
            _hotkey.HotkeyPressed += OnGlobalHotkey;
        }

        // Wire minimize/restore so the ViewModel can control the window during QR scan
        var vm = (MainViewModel)DataContext;
        vm.MinimizeWindow = () => WindowState = WindowState.Minimized;
        vm.RestoreWindow  = () => { Show(); WindowState = WindowState.Normal; Activate(); };
    }

    private void OnGlobalHotkey()
    {
        if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized)
        {
            // Already visible — hide to tray
            HideToTray();
        }
        else
        {
            ShowFromTray();
        }
    }

    // Minimize to tray instead of taskbar
    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && AppSettings.MinimizeToTray)
        {
            HideToTray();
        }
    }

    // Intercept close → hide to tray (unless truly exiting)
    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            HideToTray();
        }
        else
        {
            _hotkey.Dispose();
            TrayIcon.Dispose();
        }
    }

    private void HideToTray()
    {
        Hide();
        WindowState = WindowState.Normal; // Reset so restore works properly
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    // Tray icon handlers
    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => ShowFromTray();
    private void OnTrayShow(object sender, RoutedEventArgs e) => ShowFromTray();
    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyClosing = true;
        (DataContext as MainViewModel)?.Shutdown();
        Close();
    }

    // Menu popup
    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = !MenuPopup.IsOpen;
    }

    private void OnMoveToVaultClick(object sender, RoutedEventArgs e)
    {
        var button  = (Button)sender;
        var account = (AccountViewModel)button.DataContext;
        var mainVm  = (MainViewModel)button.Tag;

        if (mainVm.OpenVaultNames.Count == 0)
        {
            mainVm.StatusMessage = "No shared vaults are open. Open one from the menu first.";
            return;
        }

        var surface = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#313244"));
        var text    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cdd6f4"));
        var border  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#45475a"));

        var menu = new ContextMenu { Background = surface, Foreground = text, BorderBrush = border };
        foreach (var vaultName in mainVm.OpenVaultNames)
        {
            var name = vaultName;
            var item = new MenuItem
            {
                Header     = name,
                Background = surface,
                Foreground = text,
                Icon       = new TextBlock { Text = "→", FontSize = 13, Foreground = text, VerticalAlignment = VerticalAlignment.Center }
            };
            item.Click += (_, _) =>
            {
                var result = MessageBox.Show(
                    $"Move \"{account.DisplayName}\" to vault \"{name}\"?",
                    "Move to Vault",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (result == MessageBoxResult.Yes)
                    mainVm.MoveAccountToVault(account, name);
            };
            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        button.ContextMenu.IsOpen = true;
    }

    private void OnMoveToLocalClick(object sender, RoutedEventArgs e)
    {
        var button  = (Button)sender;
        var account = (AccountViewModel)button.DataContext;
        var mainVm  = (MainViewModel)button.Tag;

        var result = MessageBox.Show(
            $"Move \"{account.DisplayName}\" from vault \"{account.VaultName}\" to Personal?\n\nThe secret will be re-encrypted with your Windows user profile.",
            "Move to Personal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            mainVm.MoveAccountToLocal(account);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var button  = (Button)sender;
        var account = (AccountViewModel)button.DataContext;
        var mainVm  = (MainViewModel)button.Tag;

        var result = MessageBox.Show(
            $"Remove \"{account.DisplayName}\"?\n\nThis cannot be undone.",
            "Remove Token",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            mainVm.RemoveAccountCommand.Execute(account);
    }

    private void OnLockPasswordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            SubmitLockPassword();
    }

    private void OnUnlockWithPasswordClick(object sender, RoutedEventArgs e)
        => SubmitLockPassword();

    private void SubmitLockPassword()
    {
        var vm = (MainViewModel)DataContext;
        vm.TryUnlockWithPassword(LockPasswordBox.Password);
        LockPasswordBox.Clear();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => Close();
}
