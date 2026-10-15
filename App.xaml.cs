using System.IO;
using System.Windows;
using System.Windows.Threading;
using WardLock.Services.BrowserBridge;

namespace WardLock;

public partial class App : Application
{
    // "Local\" = per logon session: one WardLock per signed-in user.
    private const string InstanceMutexName = @"Local\WardLock.SingleInstance";
    private const string ActivateEventName = @"Local\WardLock.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private DateTime _lastRecoveredError = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Chrome/Edge launch WardLock.exe as the extension's native messaging
        // host, passing the extension origin as an argument. In that mode run
        // as a headless stdio↔pipe relay to the real app instance — no UI.
        var origin = e.Args.FirstOrDefault(
            a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
        if (origin != null)
        {
            NativeMessagingProxy.Run(origin); // blocks until the browser closes the port
            Shutdown();
            return;
        }

        // A second GUI instance would keep its own copy of accounts.json and
        // settings.json (each save overwriting the other's changes), fail to
        // register the hotkeys, and race the first for the browser-bridge
        // pipe. Hand off to the running instance instead.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try { using var activate = EventWaitHandle.OpenExisting(ActivateEventName); activate.Set(); }
            catch (WaitHandleCannotBeOpenedException) { /* first instance still starting */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => (Current.MainWindow as global::WardLock.MainWindow)?.ShowFromTray()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write(ex);
        };

        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_instanceMutex != null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Log and recover from an isolated UI-thread exception (e.g. the clipboard
    /// held by another process). A second one within a few seconds suggests a
    /// loop, so let it crash rather than keep running in an unknown state —
    /// everything is persisted on each change, so nothing is lost.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        if ((DateTime.UtcNow - _lastRecoveredError).TotalSeconds < 5) return;

        _lastRecoveredError = DateTime.UtcNow;
        e.Handled = true;
        MessageBox.Show(
            $"WardLock hit an unexpected error and recovered:\n\n{e.Exception.Message}\n\nDetails: {ErrorLog.LogPath}",
            "WardLock", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

/// <summary>Append-only local error log (exception types, messages, stack traces — never secrets).</summary>
internal static class ErrorLog
{
    private const long MaxBytes = 1024 * 1024;

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WardLock", "error.log");

    private static readonly object Gate = new();

    public static void Write(Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    File.Delete(LogPath);
                File.AppendAllText(LogPath, $"[{DateTime.UtcNow:o}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw from an exception handler.
        }
    }
}
