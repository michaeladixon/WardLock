using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace WardLock.Services.BrowserBridge;

/// <summary>
/// Runs when the browser launches WardLock.exe as its native messaging host
/// (detected from the launch arguments, see <see cref="DetectBrowserOrigin"/>).
/// Relays framed messages between the browser (stdio) and the running WardLock
/// instance (named pipe). No UI is shown in this mode.
///
/// The proxy holds no secrets and makes no decisions — lock state, origin
/// validation, and domain matching are all enforced by the app-side server.
/// </summary>
public static class NativeMessagingProxy
{
    public const string PipeName = "WardLock.BrowserBridge";

    /// <summary>
    /// The calling extension's identity if WardLock.exe was launched as a native
    /// messaging host, else null (normal GUI start).
    /// <list type="bullet">
    /// <item>Chrome/Edge on Windows pass <c>chrome-extension://&lt;id&gt;/</c> and
    /// <c>--parent-window=&lt;hwnd&gt;</c>; the origin is used as-is.</item>
    /// <item>Firefox passes the host manifest's path and the add-on ID; the ID
    /// becomes <c>firefox:&lt;id&gt;</c> (moz-extension:// origins are per-install).</item>
    /// </list>
    /// The browser already restricted who may launch the host; the app re-checks
    /// the identity against the installed manifests during the pipe handshake.
    /// </summary>
    public static string? DetectBrowserOrigin(IReadOnlyList<string> args)
    {
        var chromium = args.FirstOrDefault(
            a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
        if (chromium != null) return chromium;

        if (args.Count >= 2
            && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[1])
            && !args[1].StartsWith('-'))
            return BrowserIntegrationInstaller.FirefoxOrigin(args[1].Trim());

        return null;
    }

    public static void Run(string origin)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        NamedPipeClientStream? pipe = null;
        try
        {
            while (true)
            {
                JsonDocument? request;
                try
                {
                    request = NativeMessagingFraming.ReadMessage(stdin);
                }
                catch (Exception)
                {
                    return; // malformed frame or browser closed the port — bail out
                }
                if (request == null) return; // browser closed the port

                using (request)
                {
                    pipe ??= Connect(origin, stdout);
                    if (pipe == null) continue; // error already reported for this request

                    try
                    {
                        NativeMessagingFraming.WriteRaw(pipe, NativeMessagingFraming.ToUtf8(request));
                        var response = NativeMessagingFraming.ReadMessage(pipe);
                        if (response == null) throw new IOException("App closed the pipe.");
                        using (response)
                            NativeMessagingFraming.WriteRaw(stdout, NativeMessagingFraming.ToUtf8(response));
                    }
                    catch (Exception)
                    {
                        pipe.Dispose();
                        pipe = null;
                        try
                        {
                            NativeMessagingFraming.WriteMessage(stdout,
                                new { ok = false, error = "app-not-running" });
                        }
                        catch (IOException)
                        {
                            return; // browser already closed stdout — nothing to report to
                        }
                    }
                }
            }
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    /// <summary>
    /// Connects to the running app and performs the origin handshake. Returns null
    /// (after reporting the error to the browser) if the app isn't running or the
    /// extension origin is rejected.
    /// </summary>
    private static NamedPipeClientStream? Connect(string origin, Stream stdout)
    {
        var pipe = new NamedPipeClientStream(".", PipeName,
            PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(timeout: 2000);
            NativeMessagingFraming.WriteMessage(pipe, new { type = "hello", origin });

            using var reply = NativeMessagingFraming.ReadMessage(pipe)
                ?? throw new IOException("No handshake reply.");
            if (!reply.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = reply.RootElement.TryGetProperty("error", out var e)
                    ? e.GetString() : "handshake-rejected";
                pipe.Dispose();
                NativeMessagingFraming.WriteMessage(stdout, new { ok = false, error });
                return null;
            }
            return pipe;
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            NativeMessagingFraming.WriteMessage(stdout, new { ok = false, error = "app-not-running" });
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // The pipe exists but this (medium-integrity) proxy can't open it — the
            // app is running at a higher integrity level (launched elevated / from an
            // administrator Visual Studio) while the browser runs normally.
            pipe.Dispose();
            NativeMessagingFraming.WriteMessage(stdout, new { ok = false, error = "app-elevated" });
            return null;
        }
        catch (Exception)
        {
            // Never let the native host crash on an unexpected pipe error — that
            // surfaces as an opaque "host exited" with no actionable detail.
            pipe.Dispose();
            NativeMessagingFraming.WriteMessage(stdout, new { ok = false, error = "app-unreachable" });
            return null;
        }
    }
}
