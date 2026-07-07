using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace WardLock.Services.BrowserBridge;

/// <summary>
/// Runs when the browser launches WardLock.exe as its native messaging host
/// (detected by the chrome-extension:// origin argument Chrome/Edge pass).
/// Relays framed messages between the browser (stdio) and the running WardLock
/// instance (named pipe). No UI is shown in this mode.
///
/// The proxy holds no secrets and makes no decisions — lock state, origin
/// validation, and domain matching are all enforced by the app-side server.
/// </summary>
public static class NativeMessagingProxy
{
    public const string PipeName = "WardLock.BrowserBridge";

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
                        NativeMessagingFraming.WriteMessage(stdout,
                            new { ok = false, error = "app-not-running" });
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
