using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace WardLock.Services.BrowserBridge;

/// <summary>
/// Named-pipe server inside the running WardLock app that answers requests
/// relayed by the browser-launched proxy. Every connection must open with a
/// hello handshake whose extension origin appears in the installed host
/// manifest's allowed_origins; requests are then dispatched to the handler
/// (MainViewModel), which enforces lock state and domain matching.
/// </summary>
public sealed class BrowserBridgeServer : IDisposable
{
    private readonly Func<JsonDocument, object> _handleRequest;
    private readonly CancellationTokenSource _cts = new();

    public BrowserBridgeServer(Func<JsonDocument, object> handleRequest)
    {
        _handleRequest = handleRequest;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(NativeMessagingProxy.PipeName,
                    PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(ct);
                var connected = pipe;
                pipe = null; // ownership transferred to the client task
                _ = Task.Run(() => ServeClient(connected), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // transient pipe failure — retry accepting
                await Task.Delay(250, CancellationToken.None);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private void ServeClient(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                if (!Handshake(pipe)) return;

                while (true)
                {
                    var request = NativeMessagingFraming.ReadMessage(pipe);
                    if (request == null) return; // proxy disconnected

                    using (request)
                    {
                        object response;
                        try
                        {
                            response = _handleRequest(request);
                        }
                        catch (Exception)
                        {
                            response = new { ok = false, error = "internal-error" };
                        }
                        NativeMessagingFraming.WriteMessage(pipe, response);
                    }
                }
            }
            catch (Exception)
            {
                // proxy vanished mid-message or sent garbage — drop the connection
            }
        }
    }

    /// <summary>
    /// Validates the proxy's hello message. The browser already enforces
    /// allowed_origins when launching the host; this repeats the check app-side
    /// so a rogue local process can't skip it by talking to the pipe directly.
    /// </summary>
    private static bool Handshake(NamedPipeServerStream pipe)
    {
        using var hello = NativeMessagingFraming.ReadMessage(pipe);
        if (hello == null) return false;

        var root = hello.RootElement;
        var isHello = root.TryGetProperty("type", out var t) && t.GetString() == "hello";
        var origin = isHello && root.TryGetProperty("origin", out var o) ? o.GetString() : null;

        if (origin == null || !BrowserIntegrationInstaller.GetAllowedOrigins().Contains(origin))
        {
            NativeMessagingFraming.WriteMessage(pipe, new { ok = false, error = "origin-not-allowed" });
            return false;
        }

        NativeMessagingFraming.WriteMessage(pipe, new { ok = true });
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
