using System.Windows;
using WardLock.Services.BrowserBridge;

namespace WardLock;

public partial class App : Application
{
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

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
