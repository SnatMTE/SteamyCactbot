using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace CactBridge.Services;

/// <summary>
/// Tiny local HTTP server (plain TCP, no admin rights required) that acts as
/// a transparent reverse proxy for the Cactbot raidboss overlay.
///
/// The server fetches the remote Cactbot HTML, injects a &lt;base&gt; tag so all
/// relative assets still load from the original server, then injects the
/// relay script inline so alerts are forwarded to the plugin overlay.
///
/// The user just navigates to <see cref="OverlayUrl"/> instead of the long
/// proxy.iinact.com URL - no manual Cactbot configuration required.
/// </summary>
public sealed class RelayHttpService : IDisposable
{
    private const string RemoteBase    = "https://proxy.iinact.com/overlay/cactbot/ui/raidboss/";
    private const string RemoteHtml    = RemoteBase + "raidboss.html";
    private const string DefaultQuery  = "?alerts=1&timeline=0&OVERLAY_WS=ws://127.0.0.1:10501/ws";
    private const string TimelineDefaultQuery = "?alerts=0&timeline=1&OVERLAY_WS=ws://127.0.0.1:10501/ws";

    private static readonly HttpClient HttpClient = new();

    private readonly IPluginLog log;
    private readonly string jsFilePath;
    private readonly CancellationTokenSource cts = new();
    private TcpListener? listener;

    /// <summary>Bound port, or -1 if the server failed to start.</summary>
    public int Port { get; private set; } = -1;

    /// <summary>The URL the user should load as their Cactbot overlay (alerts mode).</summary>
    public string OverlayUrl => Port > 0
        ? $"http://127.0.0.1:{Port}/{DefaultQuery}"
        : "(server not running)";

    /// <summary>The URL for the timeline-only overlay view.</summary>
    public string TimelineOverlayUrl => Port > 0
        ? $"http://127.0.0.1:{Port}/{TimelineDefaultQuery}"
        : "(server not running)";

    public RelayHttpService(IPluginLog log, string pluginDirectory, int preferredPort = 9876)
    {
        this.log = log;
        jsFilePath = Path.Combine(pluginDirectory, "raidboss-user.js");
        TryStart(preferredPort);
    }

    // -----------------------------------------------------------------------
    // Server lifecycle
    // -----------------------------------------------------------------------

    private void TryStart(int startPort)
    {
        for (var port = startPort; port < startPort + 20; port++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Port = port;
                _ = Task.Run(() => AcceptLoopAsync(cts.Token));
                log.Information($"[CactBridge] Relay HTTP on {OverlayUrl}");
                return;
            }
            catch (SocketException)
            {
                listener?.Stop();
                listener = null;
            }
        }
        log.Warning("[CactBridge] Relay HTTP server: no port available.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener != null)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.Debug($"[CactBridge] HTTP accept: {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Request handling
    // -----------------------------------------------------------------------

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.ReceiveTimeout = 3000;
            var stream = client.GetStream();

            // Read just enough to get the request line
            var buf = new byte[8192];
            var read = await stream.ReadAsync(buf, ct);
            if (read == 0) return;

            var firstLine = Encoding.ASCII.GetString(buf, 0, read).Split('\n')[0].Trim();
            var parts     = firstLine.Split(' ');
            var method    = parts.Length > 0 ? parts[0] : "GET";
            var rawTarget = parts.Length > 1 ? parts[1] : "/";

            var qIdx  = rawTarget.IndexOf('?');
            var path  = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
            var query = qIdx >= 0 ? rawTarget[qIdx..] : string.Empty;

            // CORS pre-flight
            if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await SendHeaders(stream, 204, "No Content", "text/plain", 0, ct);
                return;
            }

            // Root → proxy Cactbot HTML with relay script injected
            if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                await ServeProxiedCactbotAsync(stream, query, ct);
                return;
            }

            // Serve raidboss-user.js for any path (backward compat / direct fetch)
            if (path.EndsWith("raidboss-user.js", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(jsFilePath))
                {
                    var body = await File.ReadAllBytesAsync(jsFilePath, ct);
                    await SendHeaders(stream, 200, "OK", "application/javascript; charset=utf-8", body.Length, ct);
                    await stream.WriteAsync(body, ct);
                }
                else
                {
                    await SendHeaders(stream, 404, "Not Found", "text/plain", 0, ct);
                }
                return;
            }

            // Everything else → 404
            await SendHeaders(stream, 404, "Not Found", "text/plain", 0, ct);
        }
        catch when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.Debug($"[CactBridge] HTTP handler: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    // -----------------------------------------------------------------------
    // Reverse proxy
    // -----------------------------------------------------------------------

    private async Task ServeProxiedCactbotAsync(NetworkStream stream, string query, CancellationToken ct)
    {
        try
        {
            // Fall back to the standard Cactbot query if the browser sent none
            if (string.IsNullOrEmpty(query))
                query = DefaultQuery;

            var remoteUrl = RemoteHtml + query;
            log.Debug($"[CactBridge] Fetching {remoteUrl}");

            var html = await HttpClient.GetStringAsync(remoteUrl, ct);

            // Inject <base> so relative asset URLs resolve back to the real server
            var baseTag = $"\n<base href=\"{RemoteBase}\">\n";
            html = InjectAfterMarker(html, "<head>", baseTag)
                ?? InjectBeforeMarker(html, "</head>", baseTag)
                ?? (baseTag + html);

            // Inject relay script inline from the JS file
            var relayJs = File.Exists(jsFilePath)
                ? await File.ReadAllTextAsync(jsFilePath, ct)
                : "console.warn('[CactBridge] raidboss-user.js not found')";
            var scriptTag = $"\n<script>\n{relayJs}\n</script>\n";
            html = InjectBeforeMarker(html, "</body>", scriptTag)
                ?? (html + scriptTag);

            var body = Encoding.UTF8.GetBytes(html);
            await SendHeaders(stream, 200, "OK", "text/html; charset=utf-8", body.Length, ct);
            await stream.WriteAsync(body, ct);
            log.Information("[CactBridge] Served proxied Cactbot page");
        }
        catch (Exception ex)
        {
            log.Warning($"[CactBridge] Proxy fetch failed: {ex.Message}");
            var msg = Encoding.UTF8.GetBytes($"<html><body>CactBridge: failed to fetch Cactbot page.<br>{ex.Message}<br>Check that IINACT/OverlayPlugin is running.</body></html>");
            await SendHeaders(stream, 502, "Bad Gateway", "text/html; charset=utf-8", msg.Length, ct);
            await stream.WriteAsync(msg, ct);
        }
    }

    private static string? InjectAfterMarker(string html, string marker, string inject)
    {
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return html.Insert(idx + marker.Length, inject);
    }

    private static string? InjectBeforeMarker(string html, string marker, string inject)
    {
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return html.Insert(idx, inject);
    }

    private static async Task SendHeaders(
        NetworkStream stream, int code, string status,
        string contentType, long contentLength, CancellationToken ct)
    {
        var headers =
            $"HTTP/1.1 {code} {status}\r\n" +
            $"Access-Control-Allow-Origin: *\r\n" +
            $"Access-Control-Allow-Headers: *\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            $"Connection: close\r\n" +
            $"\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        cts.Cancel();
        listener?.Stop();
        cts.Dispose();
    }
}
