using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using CactbotUI.Models;
using CactbotUI.Services;

namespace CactbotUI.Windows;

/// <summary>
/// Transparent, always-on-top overlay window that renders cactbot raidboss
/// alerts stacked vertically on screen.
///
/// Design goals:
///   - Zero allocations per frame during normal operation (re-uses <see cref="frameAlerts"/>)
///   - Smooth one-second fade-out on expiry (via <see cref="CactbotAlert.FadeAlpha"/>)
///   - Drag-to-reposition with position persisted to <see cref="Configuration"/>
///   - Subtle connection status indicator when no alerts are active
/// </summary>
public class OverlayWindow : Window, IDisposable
{
    // -----------------------------------------------------------------------
    // Alert-type display colours (RGBA, full alpha — alpha is modulated below)
    // -----------------------------------------------------------------------
    private static readonly Vector4 ColorAlarm  = new(1.00f, 0.15f, 0.15f, 1f); // red
    private static readonly Vector4 ColorAlert  = new(1.00f, 0.65f, 0.00f, 1f); // orange
    private static readonly Vector4 ColorInfo   = new(1.00f, 1.00f, 1.00f, 1f); // white

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly Plugin          plugin;
    private readonly WebSocketService wsService;

    // Re-used every frame to avoid per-frame heap allocation
    private readonly List<CactbotAlert> frameAlerts = new();

    // True after the window has been positioned from saved config at least once.
    // Reset in OnClose() so the saved position is re-applied on next open.
    private bool positionInitialised;
    private bool moveMode;

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------

    public OverlayWindow(Plugin plugin, WebSocketService wsService)
        : base("##CactbotOverlay",
               ImGuiWindowFlags.NoTitleBar        |
               ImGuiWindowFlags.NoResize          |
               ImGuiWindowFlags.NoScrollbar       |
               ImGuiWindowFlags.NoScrollWithMouse |
               ImGuiWindowFlags.AlwaysAutoResize  |
               ImGuiWindowFlags.NoSavedSettings   |   // we persist position ourselves
               ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin    = plugin;
        this.wsService = wsService;

        // Overlay is always active; /cactbot controls move mode only.
        IsOpen = true;
    }

    public void Dispose() { }

    // -----------------------------------------------------------------------
    // Window lifecycle hooks
    // -----------------------------------------------------------------------

    public override void PreDraw()
    {
        var cfg = plugin.Configuration;
        IsOpen = true;

        // Apply the saved screen position on the first frame after opening.
        // Combined with NoSavedSettings, ImGuiCond.Always fires on every new
        // ImGui window lifetime — which is exactly what we want after
        // OnClose() resets positionInitialised.
        if (!positionInitialised)
        {
            if (MathF.Abs(cfg.OverlayX - 100f) < 0.5f && MathF.Abs(cfg.OverlayY - 100f) < 0.5f)
            {
                var size = ImGui.GetIO().DisplaySize;
                ImGui.SetNextWindowPos(new Vector2(size.X * 0.5f, size.Y * 0.5f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            }
            else
            {
                ImGui.SetNextWindowPos(new Vector2(cfg.OverlayX, cfg.OverlayY), ImGuiCond.Always);
            }

            positionInitialised = true;
        }

        // Keep the overlay plain while optionally showing a subtle drag surface.
        ImGui.SetNextWindowBgAlpha(moveMode ? 0.15f : 0.00f);

        // Move mode can be toggled with /cactbot. When off, keep it fixed and click-through.
        if (!moveMode || cfg.LockOverlayPosition)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~ImGuiWindowFlags.NoMove;

        if (moveMode)
            Flags &= ~ImGuiWindowFlags.NoInputs;
        else
            Flags |= ImGuiWindowFlags.NoInputs;
    }

    /// <summary>
    /// Reset the position initialisation flag so that the saved config position
    /// is re-applied the next time the window opens.
    /// </summary>
    public override void OnClose()
    {
        positionInitialised = false;
    }

    /// <summary>Called by <see cref="ConfigWindow"/> to force a position reset.</summary>
    public void ResetPosition() => positionInitialised = false;

    public bool IsMoveMode => moveMode;

    public void SetMoveMode(bool enabled)
    {
        moveMode = enabled;
        IsOpen = true;
    }

    public void ToggleMoveMode()
    {
        moveMode = !moveMode;
        IsOpen = true;
    }

    // -----------------------------------------------------------------------
    // Draw
    // -----------------------------------------------------------------------

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        // ------------------------------------------------------------------
        // Collect active alerts (thread-safe; expired entries are pruned)
        // ------------------------------------------------------------------
        frameAlerts.Clear();
        frameAlerts.AddRange(wsService.GetActiveAlerts(cfg.MaxVisibleAlerts));

        // ------------------------------------------------------------------
        // Nothing to show — render a subtle drag-handle / status dot so the
        // user can still reposition the overlay outside of combat
        // ------------------------------------------------------------------
        if (frameAlerts.Count == 0)
        {
            var statusColor = wsService.IsConnected
                ? new Vector4(1.00f, 1.00f, 1.00f, moveMode ? 0.70f : 0.45f)
                : new Vector4(1.00f, 0.45f, 0.45f, moveMode ? 0.80f : 0.55f);

            ImGui.SetWindowFontScale(cfg.AlertFontScale);
            var statusText = wsService.IsConnected
                ? (wsService.LogLineCount == 0
                    ? "Connected — no log events yet (check /xlsettings)"
                    : $"Connected — {wsService.LogLineCount} events received")
                : "OverlayPlugin WebSocket disconnected";
            ImGui.TextColored(statusColor, statusText);
            ImGui.SetWindowFontScale(1.0f);
        }
        else
        {
            // --------------------------------------------------------------
            // Render each alert stacked top-to-bottom with fade-out
            // --------------------------------------------------------------
            foreach (var alert in frameAlerts)
            {
                var baseColor = GetAlertColor(alert.Type);

                // Blend the base colour's alpha with the fade factor so alerts
                // smoothly disappear in their final second
                var displayColor = new Vector4(
                    baseColor.X,
                    baseColor.Y,
                    baseColor.Z,
                    baseColor.W * alert.FadeAlpha);

                // Compute display text — both countdown and cast bar update every frame
                string displayText;
                if (alert.CountdownEndTime.HasValue)
                {
                    var remaining = (alert.CountdownEndTime.Value - DateTime.UtcNow).TotalSeconds;
                    displayText = remaining > 0 ? $"Engage in {remaining:F1}s!" : "Engage!";
                }
                else if (alert.CastEndTime.HasValue)
                {
                    var remaining = (alert.CastEndTime.Value - DateTime.UtcNow).TotalSeconds;
                    displayText = remaining > 0
                        ? $"{alert.Text} ({remaining:F1}s)"
                        : alert.Text;
                }
                else
                {
                    displayText = alert.Text;
                }

                ImGui.SetWindowFontScale(cfg.AlertFontScale);
                ImGui.TextColored(displayColor, displayText);
                ImGui.SetWindowFontScale(1.0f);
            }
        }

        if (moveMode)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 0.75f), "Move mode: drag this anchor. Use /cactbot to finish.");
        }

        // ------------------------------------------------------------------
        // Persist position when the user drags the window
        // GetWindowPos() is valid here because we are inside a Begin/End block
        // ------------------------------------------------------------------
        var pos = ImGui.GetWindowPos();
        if (MathF.Abs(pos.X - cfg.OverlayX) > 0.5f || MathF.Abs(pos.Y - cfg.OverlayY) > 0.5f)
        {
            cfg.OverlayX = pos.X;
            cfg.OverlayY = pos.Y;
            cfg.Save();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Vector4 GetAlertColor(AlertType type) => type switch
    {
        AlertType.Alarm => ColorAlarm,
        AlertType.Alert => ColorAlert,
        _               => ColorInfo
    };
}
