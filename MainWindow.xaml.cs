using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Track user activity for auto-lock
        PreviewMouseMove += OnUserActivity;
        PreviewKeyDown += OnUserActivity;
        PreviewMouseDown += OnUserActivity;
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

    // ── Idle tracking ──

    private void OnUserActivity(object sender, EventArgs e)
    {
        ((MainViewModel)DataContext).ResetIdleTimer();
    }

    // ── Keyboard shortcuts ──

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F → focus search box
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        // Escape while in search → clear and unfocus
        else if (e.Key == Key.Escape && SearchBox.IsFocused)
        {
            var vm = (MainViewModel)DataContext;
            vm.SearchText = string.Empty;
            // Move focus away from search box
            FocusManager.SetFocusedElement(this, this);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ── Hotkey / tray ──

    private void OnGlobalHotkey()
    {
        if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized)
            HideToTray();
        else
            ShowFromTray();
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && AppSettings.MinimizeToTray)
            HideToTray();
    }

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
        WindowState = WindowState.Normal;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    // ── Tray icon handlers ──

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => ShowFromTray();
    private void OnTrayShow(object sender, RoutedEventArgs e) => ShowFromTray();
    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        _reallyClosing = true;
        (DataContext as MainViewModel)?.Shutdown();
        Close();
    }

    // ── Menu popup ──

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = !MenuPopup.IsOpen;
    }

    // ── Account context menu handlers ──

    private void OnContextCopyCode(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is AccountViewModel vm)
            vm.CopyToClipboardCommand.Execute(null);
    }

    private void OnContextMoveToVault(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not AccountViewModel account) return;
        var mainVm = (MainViewModel)DataContext;

        if (mainVm.OpenVaultNames.Count == 0)
        {
            mainVm.StatusMessage = "No shared vaults are open. Open one from the menu first.";
            return;
        }

        if (mainVm.OpenVaultNames.Count == 1)
        {
            // Only one vault open — move directly with confirmation
            var name = mainVm.OpenVaultNames[0];
            var result = MessageBox.Show(
                $"Move \"{account.DisplayName}\" to vault \"{name}\"?",
                "Move to Vault", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
                mainVm.MoveAccountToVault(account, name);
            return;
        }

        // Multiple vaults — show submenu picker
        var surface = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#313244"));
        var text = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cdd6f4"));
        var border = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#45475a"));

        var menu = new ContextMenu { Background = surface, Foreground = text, BorderBrush = border };
        foreach (var vaultName in mainVm.OpenVaultNames)
        {
            var name = vaultName;
            var item = new MenuItem { Header = name, Background = surface, Foreground = text };
            item.Click += (_, _) =>
            {
                var r = MessageBox.Show(
                    $"Move \"{account.DisplayName}\" to vault \"{name}\"?",
                    "Move to Vault", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (r == MessageBoxResult.Yes)
                    mainVm.MoveAccountToVault(account, name);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnContextMoveToLocal(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not AccountViewModel account) return;
        var mainVm = (MainViewModel)DataContext;

        var result = MessageBox.Show(
            $"Move \"{account.DisplayName}\" from vault \"{account.VaultName}\" to Personal?\n\nThe secret will be re-encrypted with your Windows user profile.",
            "Move to Personal", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            mainVm.MoveAccountToLocal(account);
    }

    private void OnContextDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not AccountViewModel account) return;
        var mainVm = (MainViewModel)DataContext;

        var result = MessageBox.Show(
            $"Remove \"{account.DisplayName}\"?\n\nThis cannot be undone.",
            "Remove Token", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            mainVm.RemoveAccountCommand.Execute(account);
    }

    // ── Existing button click handlers ──

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

    private void OnLockPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
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
