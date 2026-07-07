using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WardLock.Services;

namespace WardLock.Views;

/// <summary>
/// Shows a vault's tamper-evident audit trail: chain-integrity banner,
/// entry list (newest first), and CSV export for compliance.
/// </summary>
public partial class AuditLogWindow : Window
{
    private readonly string _vaultName;
    private readonly VaultAuditLog _log;

    public AuditLogWindow(string vaultName, VaultAuditLog log)
    {
        InitializeComponent();
        _vaultName = vaultName;
        _log = log;
        TitleText.Text = $"Audit log — {vaultName}";
        Load();
    }

    private void Load()
    {
        var result = _log.Read();

        if (result.ChainIntact)
        {
            ChainBanner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e3a1e"));
            ChainText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#a6e3a1"));
            ChainText.Text = $"✓ Hash chain intact — {result.Entries.Count} entries verified";
        }
        else
        {
            ChainBanner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3a1e1e"));
            ChainText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f38ba8"));
            ChainText.Text = $"⚠ TAMPER WARNING: {result.Problem}";
        }

        EntryList.ItemsSource = result.Entries
            .OrderByDescending(e => e.Seq)
            .Select(e => new AuditRowView(e))
            .ToList();
        CountText.Text = $"{result.Entries.Count} entries · {_log.LogPath}";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Load();

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Audit Log",
            Filter = "CSV|*.csv",
            FileName = $"{_vaultName}-audit-{DateTime.Now:yyyy-MM-dd}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _log.ExportCsv(dlg.FileName);
            CountText.Text = $"Exported to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "WardLock",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Display row for one audit entry.</summary>
public sealed class AuditRowView
{
    private readonly AuditEntry _e;

    public AuditRowView(AuditEntry e) => _e = e;

    public long Seq => _e.Seq;
    public string User => _e.User;
    public string Action => _e.Action;

    public string LocalTime =>
        DateTime.TryParse(_e.Utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc)
            ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : _e.Utc;

    public string TargetDetail => string.IsNullOrEmpty(_e.Detail)
        ? _e.Target
        : $"{_e.Target} — {_e.Detail}";

    /// <summary>Code-access events yellow, structural changes mauve, lifecycle grey.</summary>
    public string ActionColor => _e.Action switch
    {
        nameof(AuditAction.CodeCopied) or
        nameof(AuditAction.CodeAutoTyped) or
        nameof(AuditAction.CodeFilledInBrowser) => "#f9e2af",
        nameof(AuditAction.AccountAdded) or
        nameof(AuditAction.AccountRemoved) or
        nameof(AuditAction.DomainChanged) => "#cba6f7",
        _ => "#a6adc8",
    };
}
