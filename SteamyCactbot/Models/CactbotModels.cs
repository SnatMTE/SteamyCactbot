using System;
using System.Text.Json.Serialization;

namespace CactbotUI.Models;

// ---------------------------------------------------------------------------
// Domain model
// ---------------------------------------------------------------------------

/// <summary>Severity level matching cactbot raidboss trigger types.</summary>
public enum AlertType
{
    Info,
    Alert,
    Alarm
}

/// <summary>
/// A processed raidboss alert ready for display on screen.
/// Instances are produced by <see cref="CactbotUI.Services.WebSocketService"/>
/// and consumed each frame by <see cref="CactbotUI.Windows.OverlayWindow"/>.
/// </summary>
public class CactbotAlert
{
    /// <summary>Human-readable text to display (e.g. "Stack!", "Spread").</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Severity - determines display colour.</summary>
    public AlertType Type { get; set; } = AlertType.Info;

    /// <summary>How many seconds this alert should stay on screen.</summary>
    public float Duration { get; set; } = 3.0f;

    /// <summary>UTC timestamp at which this alert was received.</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When set, this alert is a live countdown. The overlay will display the
    /// remaining seconds computed each frame instead of the static <see cref="Text"/>.
    /// </summary>
    public DateTime? CountdownEndTime { get; set; }

    /// <summary>
    /// When set, this alert is a live cast bar. The overlay appends the remaining
    /// cast time each frame: "{Text} (X.Xs)". Expires when <see cref="IsExpired"/> is true.
    /// </summary>
    public DateTime? CastEndTime { get; set; }

    /// <summary>Seconds elapsed since this alert arrived.</summary>
    public float ElapsedSeconds => (float)(DateTime.UtcNow - ReceivedAt).TotalSeconds;

    /// <summary>True when the alert has exceeded its display duration.</summary>
    public bool IsExpired => ElapsedSeconds >= Duration;

    /// <summary>
    /// 0–1 alpha multiplier for a smooth one-second fade-out.
    /// Returns 1 while the alert still has more than one second remaining.
    /// </summary>
    public float FadeAlpha
    {
        get
        {
            if (Duration <= 1f) return 1f;
            var remaining = Duration - ElapsedSeconds;
            return remaining < 1f ? Math.Clamp(remaining, 0f, 1f) : 1f;
        }
    }
}

// ---------------------------------------------------------------------------
// OverlayPlugin WebSocket wire types
// ---------------------------------------------------------------------------

/// <summary>
/// Top-level shape of every message received from the OverlayPlugin WebSocket.
/// Only the fields we actually use are deserialised - unknown fields are ignored.
/// </summary>
internal class OverlayPluginMessage
{
    /// <summary>Event type string, e.g. "onBroadcastMessage", "ChangeZone".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Source overlay name, present on broadcast messages.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Payload for <c>onBroadcastMessage</c> events sent by the cactbot
    /// raidboss overlay. Contains the processed alert data.
    /// </summary>
    [JsonPropertyName("msg")]
    public BroadcastPayload? Msg { get; set; }

    // ChangeZone fields
    [JsonPropertyName("zoneID")]
    public int? ZoneId { get; set; }

    [JsonPropertyName("zoneName")]
    public string? ZoneName { get; set; }
}

/// <summary>
/// The inner payload of an <c>onBroadcastMessage</c> event.
/// Cactbot's raidboss overlay sends this when a trigger fires.
/// Shape: <c>{ type: "alarm"|"alert"|"info", text: "…", duration?: number }</c>
/// </summary>
internal class BroadcastPayload
{
    /// <summary>Alert severity: "alarm", "alert", or "info".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>The text to display on screen.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Optional display duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public float? Duration { get; set; }
}

/// <summary>
/// Subscription request sent to OverlayPlugin immediately after connecting.
/// Format: <c>{ "call": "subscribe", "events": [ … ] }</c>
/// </summary>
internal class SubscribeRequest
{
    [JsonPropertyName("call")]
    public string Call { get; set; } = "subscribe";

    [JsonPropertyName("events")]
    public string[] Events { get; set; } = Array.Empty<string>();
}
