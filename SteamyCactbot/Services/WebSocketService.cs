using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using CactbotUI.Models;

namespace CactbotUI.Services;

/// <summary>
/// Manages a persistent, auto-reconnecting WebSocket connection to the
/// OverlayPlugin endpoint at <c>ws://127.0.0.1:10501/ws</c>.
///
/// On connect the service subscribes to:
///   - <c>onBroadcastMessage</c>  - processed raidboss trigger alerts from cactbot
///   - <c>ChangeZone</c>          - zone transitions for status display
///   - <c>onInCombat</c>          - combat state changes
///
/// All network I/O runs on a background <see cref="Task"/>; the public API is
/// thread-safe and may be called from the ImGui draw loop without blocking.
/// </summary>
public sealed class WebSocketService : IDisposable
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    private const string WsUrl           = "ws://127.0.0.1:10501/ws";
    private const int    MaxStoredAlerts  = 20;
    private const int    MaxDebugMessages = 20;
    private const int    ReconnectDelayMs = 5_000;

    // Events confirmed to be registered in IINACT OverlayPlugin 0.19.x.
    // "onBroadcastMessage" and "onInCombat" do NOT exist in this build -
    // onBroadcastMessage only fires when a browser-based cactbot overlay is
    // running and broadcasting; IINACT's builtin Cactbot event source does not
    // generate it because it does not run the raidboss JS layer.
    private static readonly string[] SubscribedEvents =
    [
        "ChangeZone",
        "LogLine",
        "ImportedLogLines",
        "BroadcastMessage",
        "onZoneChangedEvent",
        "onLogEvent",
        "CombatData"
    ];

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private readonly IPluginLog          log;
    private readonly Configuration       config;
    private readonly object              alertLock   = new();
    private readonly object              debugLock   = new();
    private readonly List<CactbotAlert>  alerts      = new();
    private readonly List<string>        debugMessages = new();
    private readonly CancellationTokenSource cts     = new();
    private readonly ConcurrentQueue<string> chatQueue = new();
    private readonly System.Collections.Generic.HashSet<string> seenTypes = new();
    private int              logLineCount;

    private ClientWebSocket? socket;
    private bool             disposed;

    // -----------------------------------------------------------------------
    // Public properties (safe to read from any thread)
    // -----------------------------------------------------------------------

    /// <summary>True when the WebSocket is in the <see cref="WebSocketState.Open"/> state.</summary>
    public bool IsConnected => socket?.State == WebSocketState.Open;

    /// <summary>Name of the current FFXIV zone, updated on each ChangeZone event.</summary>
    public string CurrentZone { get; private set; } = string.Empty;

    /// <summary>Total number of <c>LogLine</c> WebSocket events received since connecting. Use this to verify data is flowing.</summary>
    public int LogLineCount => logLineCount;

    /// <summary>
    /// Returns a snapshot of the last <see cref="MaxDebugMessages"/> raw JSON messages
    /// received from the WebSocket, newest first. Use this to diagnose unexpected payload shapes.
    /// </summary>
    public List<string> GetDebugMessages()
    {
        lock (debugLock)
            return new List<string>(debugMessages);
    }

    // -----------------------------------------------------------------------
    // Constructor / startup
    // -----------------------------------------------------------------------

    public WebSocketService(IPluginLog log, Configuration config)
    {
        this.log    = log;
        this.config = config;
        // Fire-and-forget; the loop manages its own lifetime via cts
        _ = Task.Run(() => RunLoopAsync(cts.Token));
    }

    // -----------------------------------------------------------------------
    // Public API (UI thread)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a point-in-time snapshot of active (non-expired) alerts trimmed
    /// to <paramref name="maxCount"/> most-recent entries. Expired alerts are
    /// pruned from the internal list during this call. Thread-safe.
    /// </summary>
    public List<CactbotAlert> GetActiveAlerts(int maxCount)
    {
        lock (alertLock)
        {
            // Prune expired entries while we hold the lock
            alerts.RemoveAll(a => a.IsExpired);

            var start = Math.Max(0, alerts.Count - maxCount);
            return alerts.GetRange(start, alerts.Count - start);
        }
    }

    // -----------------------------------------------------------------------
    // Background reconnect loop
    // -----------------------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warning($"[CactbotPlugin] WebSocket error: {ex.Message}");
            }

            if (!token.IsCancellationRequested)
            {
                log.Debug($"[CactbotPlugin] Reconnecting in {ReconnectDelayMs / 1000}s…");
                try { await Task.Delay(ReconnectDelayMs, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        log.Information("[CactbotPlugin] WebSocket loop exited.");
    }

    // -----------------------------------------------------------------------
    // Connect → subscribe → receive loop
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens the WebSocket, sends a subscription message, then reads frames
    /// in a loop until the connection closes or the token is cancelled.
    /// </summary>
    private async Task ConnectAndReceiveAsync(CancellationToken token)
    {
        // Dispose any previous socket before creating a fresh one
        socket?.Dispose();
        socket = new ClientWebSocket();

        log.Information($"[CactbotPlugin] Connecting to {WsUrl}…");
        await socket.ConnectAsync(new Uri(WsUrl), token);
        log.Information("[CactbotPlugin] Connected to OverlayPlugin WebSocket.");

        // ------------------------------------------------------------------
        // Subscribe to desired events
        // ------------------------------------------------------------------
        var subJson  = JsonSerializer.Serialize(new SubscribeRequest { Events = SubscribedEvents });
        var subBytes = Encoding.UTF8.GetBytes(subJson);
        await socket.SendAsync(
            new ArraySegment<byte>(subBytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: token);

        // ------------------------------------------------------------------
        // Receive loop - reassemble fragmented frames before processing
        // ------------------------------------------------------------------
        var buffer         = new byte[16 * 1024]; // 16 KB per frame
        var messageBuilder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            messageBuilder.Clear();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    log.Information("[CactbotPlugin] Server requested connection close.");
                    // Respond with a close frame and exit
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closed",
                        CancellationToken.None);
                    return;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var raw = messageBuilder.ToString();
            StoreDebugMessage(raw);
            HandleMessage(raw);
        }
    }

    // -----------------------------------------------------------------------
    // Message handling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Top-level dispatcher: deserialises the JSON envelope and routes to the
    /// appropriate handler based on the <c>type</c> field.
    /// </summary>
    private void StoreDebugMessage(string raw)
    {
        // Truncate very long messages so they don't swamp the UI
        var display = raw.Length > 300 ? raw[..300] + "…" : raw;
        lock (debugLock)
        {
            debugMessages.Insert(0, display);
            if (debugMessages.Count > MaxDebugMessages)
                debugMessages.RemoveAt(debugMessages.Count - 1);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return;

            var type = typeElement.GetString();
            if (string.IsNullOrEmpty(type))
                return;

            // Log each new message type at INFO level once so user can see what's flowing
            if (seenTypes.Add(type))
                log.Information($"[CactbotPlugin] First message of type: {type}");

            switch (type)
            {
                case "BroadcastMessage":
                case "onBroadcastMessage":
                    HandleBroadcast(root);
                    break;

                case "ChangeZone":
                case "onZoneChangedEvent":
                    var prevZone = CurrentZone;
                    if (root.TryGetProperty("zoneName", out var zoneEl) && zoneEl.ValueKind == JsonValueKind.String)
                        CurrentZone = zoneEl.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("zoneID", out var zoneIdEl))
                        CurrentZone = $"Zone {zoneIdEl}";
                    if (!string.IsNullOrWhiteSpace(CurrentZone) && CurrentZone != prevZone)
                        EnqueueAlert($"Zone: {CurrentZone}", AlertType.Info, 4f);
                    log.Information($"[CactbotPlugin] Zone changed: {CurrentZone}");
                    break;

                case "LogLine":
                    Interlocked.Increment(ref logLineCount);
                    // OverlayPlugin sends "rawLine" as the pipe-delimited string;
                    // "line" is a JSON array and cannot be used as a string directly.
                    if (root.TryGetProperty("rawLine", out var rawLineEl) && rawLineEl.ValueKind == JsonValueKind.String)
                        HandleActLogLine(rawLineEl.GetString() ?? string.Empty);
                    else if (root.TryGetProperty("line", out var lineArrEl) && lineArrEl.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var el in lineArrEl.EnumerateArray())
                            parts.Add(el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString());
                        HandleActLogLine(string.Join("|", parts));
                    }
                    break;

                case "ImportedLogLines":
                    // Cactbot test mode injects synthetic log lines through this event.
                    // Field is "logLines" (string array of raw pipe-delimited ACT log lines).
                    if (root.TryGetProperty("logLines", out var importedLines) && importedLines.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in importedLines.EnumerateArray())
                        {
                            if (entry.ValueKind == JsonValueKind.String)
                            {
                                var importedLine = entry.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(importedLine))
                                {
                                    Interlocked.Increment(ref logLineCount);
                                    HandleActLogLine(importedLine);
                                }
                            }
                        }
                    }
                    break;

                case "onLogEvent":
                    if (root.TryGetProperty("detail", out var detail) && detail.TryGetProperty("logs", out var logs)
                        && logs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in logs.EnumerateArray())
                            if (entry.ValueKind == JsonValueKind.String)
                                HandleActLogLine(entry.GetString() ?? string.Empty);
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            log.Verbose($"[CactbotPlugin] JSON parse failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            log.Warning($"[CactbotPlugin] Unexpected error in HandleMessage: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes an <c>onBroadcastMessage</c> event.
    ///
    /// Cactbot's raidboss overlay broadcasts processed trigger alerts here with
    /// the payload shape: <c>{ type: "alarm"|"alert"|"info", text: "…", duration?: number }</c>
    ///
    /// This is the primary data source for on-screen alerts.
    /// </summary>
    // -----------------------------------------------------------------------
    // ACT LogLine parsing
    // -----------------------------------------------------------------------

    // ACT network log line format: "typeCode|timestamp|field1|field2|..."
    // Type codes are decimal integers.
    // Common codes:
    //   20  = NetworkStartsCasting
    //   25  = NetworkDeath
    //   27  = NetworkTargetIcon (headmarker)
    //   33  = NetworkTether
    //   268 = Countdown (OverlayPlugin synthetic)
    //   269 = CountdownCancel (OverlayPlugin synthetic)
    private void HandleActLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var parts = line.Split('|');
        if (parts.Length < 2) return;
        if (!int.TryParse(parts[0], out var typeCode)) return;

        switch (typeCode)
        {
            case 20: // NetworkStartsCasting: [20|ts|sourceId|sourceName|abilityId|abilityName|targetId|targetName|castTime|...]
                if (parts.Length >= 8)
                {
                    var srcId       = parts[2];
                    var srcName     = parts[3];
                    var abilityName = parts[5];
                    // Players have IDs like 1xxxxxxx; environment dummy is E0000000.
                    // Everything else with an 8-char hex ID is an NPC/enemy.
                    var isPlayer = srcId.StartsWith("1", StringComparison.OrdinalIgnoreCase) && srcId.Length == 8;
                    var isEnv    = string.Equals(srcId, "E0000000", StringComparison.OrdinalIgnoreCase);
                    var isNpc    = !string.IsNullOrWhiteSpace(srcId) && !isPlayer && !isEnv;
                    if (isNpc && !string.IsNullOrWhiteSpace(abilityName) && !string.IsNullOrWhiteSpace(srcName))
                    {
                        // Parse the actual cast duration from field [8]; fall back to 4s
                        var castSeconds = parts.Length >= 9 &&
                            float.TryParse(parts[8], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var ct) && ct > 0f
                            ? ct : 4f;
                        EnqueueAlert(new CactbotAlert
                        {
                            Text        = $"{srcName}: {abilityName}",
                            Type        = AlertType.Alert,
                            Duration    = castSeconds + 1f,  // 1s linger after cast completes
                            ReceivedAt  = DateTime.UtcNow,
                            CastEndTime = DateTime.UtcNow.AddSeconds(castSeconds)
                        });
                    }
                }
                break;

            case 27: // NetworkTargetIcon (headmarker): [27|ts|?|targetName|markerId|...]
                if (parts.Length >= 5)
                {
                    var target = parts[3];
                    var markerId = parts[4];
                    if (!string.IsNullOrWhiteSpace(target))
                        EnqueueAlert($"Headmarker on {target} ({markerId})", AlertType.Alert, 4f);
                }
                break;

            case 33: // NetworkTether: [33|ts|?|sourceName|?|?|targetName|...]
                if (parts.Length >= 7)
                {
                    var src = parts[3];
                    var tgt = parts[6];
                    if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(tgt))
                        EnqueueAlert($"Tether: {src} → {tgt}", AlertType.Info, 3f);
                }
                break;

            case 268: // Countdown: [268|ts|playerId|worldId|countdownTime|result|playerName|...]
                // result: "0" = countdown started, non-zero = engage
                if (parts.Length >= 5 && int.TryParse(parts[4], out var countdownSecs))
                {
                    var alertType = countdownSecs <= 3 ? AlertType.Alarm
                        : countdownSecs <= 5 ? AlertType.Alert
                        : AlertType.Info;
                    var endTime = DateTime.UtcNow.AddSeconds(countdownSecs);
                    EnqueueAlert(new CactbotAlert
                    {
                        Text = $"Engage in {countdownSecs}s!",
                        Type = alertType,
                        Duration = (float)countdownSecs + 2f,
                        ReceivedAt = DateTime.UtcNow,
                        CountdownEndTime = endTime
                    });
                }
                break;

            case 269: // CountdownCancel: [269|ts|playerId|worldId|playerName|...]
                EnqueueAlert("Countdown canceled", AlertType.Info, 3f);
                break;
        }
    }

    private void HandleBroadcast(JsonElement root)
    {
        if (!root.TryGetProperty("msg", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;

        // Common raidboss shape: { type: "alarm"|"alert"|"info", text: "...", duration?: n }
        if (TryGetString(payload, "text", out var baseText) && !string.IsNullOrWhiteSpace(baseText))
        {
            var rawType = TryGetString(payload, "type", out var typeText) ? typeText : "info";
            var alertType = ParseAlertType(rawType);
            var duration = TryGetFloat(payload, "duration", out var parsedDuration)
                ? parsedDuration
                : DefaultDuration(alertType);

            EnqueueAlert(baseText.Trim(), alertType, duration);
        }

        // Alternative shape used by some cactbot flows.
        AddNamedAlert(payload, "alarmText", AlertType.Alarm, 5f);
        AddNamedAlert(payload, "alertText", AlertType.Alert, 4f);
        AddNamedAlert(payload, "infoText", AlertType.Info, 3f);
        AddNamedAlert(payload, "tts", AlertType.Info, 3f);
    }

    private void AddNamedAlert(JsonElement payload, string propertyName, AlertType type, float defaultDuration)
    {
        if (TryGetString(payload, propertyName, out var text) && !string.IsNullOrWhiteSpace(text))
            EnqueueAlert(text.Trim(), type, defaultDuration);
    }

    private void EnqueueAlert(CactbotAlert alert)
    {
        lock (alertLock)
        {
            alerts.Add(alert);

            // Bound the list so memory usage stays predictable
            if (alerts.Count > MaxStoredAlerts)
                alerts.RemoveRange(0, alerts.Count - MaxStoredAlerts);
        }

        if (config.OutputToChatAnnouncement)
            chatQueue.Enqueue(alert.Text);

        log.Debug($"[CactbotPlugin] [{alert.Type}] \"{alert.Text}\" ({alert.Duration:F1}s)");
    }

    /// <summary>
    /// Dequeues one pending chat announcement message. Returns <c>false</c> when the queue is empty.
    /// Call this from the game's main thread (e.g. <c>IFramework.Update</c>) to safely forward
    /// messages to <c>IChatGui</c>.
    /// </summary>
    public bool TryDequeueChat(out string message) => chatQueue.TryDequeue(out message!);

    private void EnqueueAlert(string text, AlertType alertType, float duration)
    {
        EnqueueAlert(new CactbotAlert
        {
            Text = text,
            Type = alertType,
            Duration = Math.Max(0.5f, duration),
            ReceivedAt = DateTime.UtcNow
        });
    }

    private static bool TryGetString(JsonElement obj, string propertyName, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(propertyName, out var property))
            return false;

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                value = property.GetString();
                return !string.IsNullOrWhiteSpace(value);

            case JsonValueKind.Object:
            {
                // Some payloads localize text in a map, prefer English then first string.
                if (property.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                {
                    value = en.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                var firstString = property
                    .EnumerateObject()
                    .FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);

                if (firstString.Value.ValueKind == JsonValueKind.String)
                {
                    value = firstString.Value.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                return false;
            }

            default:
                return false;
        }
    }

    private static bool TryGetFloat(JsonElement obj, string propertyName, out float value)
    {
        value = 0f;
        if (!obj.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetSingle(out value);

        if (property.ValueKind == JsonValueKind.String && float.TryParse(property.GetString(), out value))
            return true;

        return false;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AlertType ParseAlertType(string? raw) => raw?.ToLowerInvariant() switch
    {
        "alarm" => AlertType.Alarm,
        "alert" => AlertType.Alert,
        "info"  => AlertType.Info,
        _       => AlertType.Info
    };

    private static float DefaultDuration(AlertType type) => type switch
    {
        AlertType.Alarm => 5f,
        AlertType.Alert => 4f,
        _               => 3f
    };

    // -----------------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        cts.Cancel();

        // Attempt a clean WebSocket close (best effort - we're shutting down)
        try
        {
            if (socket?.State == WebSocketState.Open)
                socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Plugin unloading",
                    CancellationToken.None)
                .Wait(millisecondsTimeout: 1_000);
        }
        catch { /* swallow - plugin is being disposed */ }

        socket?.Dispose();
        cts.Dispose();
    }
}
