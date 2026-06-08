using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using CactBridge.Models;
using CactBridge.Services;

namespace CactBridge.Windows;

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
    // Alert-type display colours (RGBA, full alpha - alpha is modulated below)
    // -----------------------------------------------------------------------
    private static readonly Vector4 ColorAlarm  = new(1.00f, 0.15f, 0.15f, 1f); // red
    private static readonly Vector4 ColorAlert  = new(1.00f, 0.65f, 0.00f, 1f); // orange
    private static readonly Vector4 ColorInfo   = new(1.00f, 1.00f, 1.00f, 1f); // white

    // Padding inside the overlay box
    private const float BoxPadding = 12f;

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly Plugin          plugin;
    private readonly WebSocketService wsService;
    private IFontHandle? axisFontHandle;
    private IFontHandle? jupiterFontHandle;
    private IFontHandle? trumpGothicFontHandle;

    // Tracks the last-seen font scale & preset so we can rebuild font handles
    // when they change (the cached handles embed the scale at creation time).
    private float lastFontScale;
    private AlertFontPreset lastFontPreset;

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
        : base("##CactBridgeOverlay",
               ImGuiWindowFlags.NoTitleBar        |
               ImGuiWindowFlags.NoScrollbar       |
               ImGuiWindowFlags.NoScrollWithMouse |
               ImGuiWindowFlags.NoSavedSettings   |   // we persist position ourselves
               ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin    = plugin;
        this.wsService = wsService;

        // Overlay is always active; /cactbridge controls move mode only.
        IsOpen = true;
    }

    public void Dispose()
    {
        axisFontHandle?.Dispose();
        jupiterFontHandle?.Dispose();
        trumpGothicFontHandle?.Dispose();
    }

    // -----------------------------------------------------------------------
    // Window lifecycle hooks
    // -----------------------------------------------------------------------

    public override void PreDraw()
    {
        var cfg = plugin.Configuration;
        IsOpen = cfg.EnableCactbotOverlay;

        // Rebuild font handles when the user changes font scale or preset
        // in the config window, since the cached handles embed the scale.
        if (MathF.Abs(cfg.AlertFontScale - lastFontScale) > 0.01f || cfg.AlertFontPreset != lastFontPreset)
        {
            InvalidateFontHandles();
            lastFontScale = cfg.AlertFontScale;
            lastFontPreset = cfg.AlertFontPreset;
        }

        // Apply the saved screen position on the first frame after opening.
        // Combined with NoSavedSettings, ImGuiCond.Always fires on every new
        // ImGui window lifetime - which is exactly what we want after
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

        // Always apply the configured box size so changes from the config window
        // take effect immediately.
        ImGui.SetNextWindowSize(new Vector2(cfg.OverlayWidth, cfg.OverlayHeight), ImGuiCond.Always);

        // Keep the overlay plain while optionally showing a subtle drag surface.
        ImGui.SetNextWindowBgAlpha(moveMode ? 0.15f : 0.00f);

        // Move mode can be toggled with /cactbridge. When off, keep it fixed and click-through.
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
        using var fontPush = PushConfiguredAlertFont(cfg.AlertFontPreset, cfg.AlertFontScale);
        var showPreview = plugin.IsConfigUiOpen;

        // ------------------------------------------------------------------
        // Collect active alerts (thread-safe; expired entries are pruned)
        // ------------------------------------------------------------------
        frameAlerts.Clear();
        if (showPreview)
        {
            frameAlerts.AddRange(GetPreviewAlerts());
        }
        else
        {
            frameAlerts.AddRange(wsService.GetActiveAlerts(cfg.MaxVisibleAlerts));
        }

        // ------------------------------------------------------------------
        // Box dimensions for wrapping and centering
        // ------------------------------------------------------------------
        var boxSize = ImGui.GetWindowSize();
        var wrapWidth = Math.Max(1f, boxSize.X - BoxPadding * 2f);
        var lineHeight = ImGui.GetFontSize() * cfg.AlertFontScale;

        // ------------------------------------------------------------------
        // Nothing to show - render a subtle drag-handle / status dot so the
        // user can still reposition the overlay outside of combat
        // ------------------------------------------------------------------
        if (frameAlerts.Count == 0)
        {
            if (moveMode)
            {
                ImGui.SetCursorPos(new Vector2(BoxPadding, BoxPadding));
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 0.75f), "Move mode: drag to reposition. Use /cactbridge to finish.");
            }
        }
        else
        {
            // --------------------------------------------------------------
            // Render each alert centered in the box, stacked vertically.
            // Each alert's text wraps to fit the box width, with every line
            // individually centered horizontally and the block centered
            // vertically within the box.
            // Measure total height of all alert text blocks
            var totalHeight = 0f;
            var textSizes = new Vector2[frameAlerts.Count];
            for (int i = 0; i < frameAlerts.Count; i++)
            {
                var text = BuildDisplayText(frameAlerts[i]);
                textSizes[i] = ImGui.CalcTextSize(text, false, wrapWidth);
                totalHeight += textSizes[i].Y;
                if (i > 0) totalHeight += 4f;
            }

            // Vertically center the alert block stack in the box
            var cursorY = Math.Max(BoxPadding, (boxSize.Y - totalHeight) * 0.5f);
            var windowPos = ImGui.GetWindowPos();

            for (int i = 0; i < frameAlerts.Count; i++)
            {
                var alert = frameAlerts[i];
                var text = BuildDisplayText(alert);
                var textSize = textSizes[i];

                // Determine base colour – per-type or custom override
                var baseColor = cfg.UseCustomAlertColor ? cfg.AlertTextColor : GetAlertColor(alert.Type);
                var displayColor = new Vector4(
                    baseColor.X, baseColor.Y, baseColor.Z,
                    baseColor.W * alert.FadeAlpha);

                // Wrap the text into individual lines for per-line centering
                var lines = WrapTextToWidth(text, wrapWidth, lineHeight);
                var colorU32 = ImGui.ColorConvertFloat4ToU32(displayColor);
                var drawList = ImGui.GetWindowDrawList();

                for (int l = 0; l < lines.Count; l++)
                {
                    var lineWidth = ImGui.CalcTextSize(lines[l]).X;
                    // Center each line horizontally within the box
                    var lineX = Math.Max(0f, (boxSize.X - lineWidth) * 0.5f);
                    var lineY = cursorY + l * lineHeight;
                    var screenPos = windowPos + new Vector2(lineX, lineY);

                    if (cfg.AlertTextOutline)
                    {
                        var outlineU32 = ImGui.ColorConvertFloat4ToU32(cfg.AlertOutlineColor);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                                if (dx != 0 || dy != 0)
                                    drawList.AddText(screenPos + new Vector2(dx, dy), outlineU32, lines[l]);
                    }

                    drawList.AddText(screenPos, colorU32, lines[l]);
                }

                cursorY += textSize.Y + 4f;
            }
        }

        // ------------------------------------------------------------------
        // Persist position AND size when the user drags/resizes the window
        // ------------------------------------------------------------------
        var pos = ImGui.GetWindowPos();
        if (MathF.Abs(pos.X - cfg.OverlayX) > 0.5f || MathF.Abs(pos.Y - cfg.OverlayY) > 0.5f)
        {
            cfg.OverlayX = pos.X;
            cfg.OverlayY = pos.Y;
            cfg.Save();
        }

        var currentSize = ImGui.GetWindowSize();
        if (MathF.Abs(currentSize.X - cfg.OverlayWidth) > 0.5f || MathF.Abs(currentSize.Y - cfg.OverlayHeight) > 0.5f)
        {
            cfg.OverlayWidth = currentSize.X;
            cfg.OverlayHeight = currentSize.Y;
            cfg.Save();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Disposes all cached font handles so they are rebuilt with the
    /// current <see cref="Configuration.AlertFontScale"/> on the next frame.
    /// </summary>
    private void InvalidateFontHandles()
    {
        axisFontHandle?.Dispose();
        axisFontHandle = null;
        jupiterFontHandle?.Dispose();
        jupiterFontHandle = null;
        trumpGothicFontHandle?.Dispose();
        trumpGothicFontHandle = null;
    }

    public IDisposable? PushConfiguredAlertFont(AlertFontPreset preset, float fontScale)
    {
        var ui = Plugin.PluginInterface.UiBuilder;

        // Base font sizes chosen to produce a crisp 1:1 render at typical display sizes.
        // fontScale then selects a larger or smaller variant so bitmap fonts stay pixel-perfect.
        return preset switch
        {
            AlertFontPreset.DalamudDefault => ui.DefaultFontHandle.Push(),
            AlertFontPreset.DalamudMono => ui.MonoFontHandle.Push(),
            AlertFontPreset.FfxivJupiter => GetOrCreateJupiterFontHandle(fontScale).Push(),
            AlertFontPreset.FfxivTrumpGothic => GetOrCreateTrumpGothicFontHandle(fontScale).Push(),
            _ => GetOrCreateAxisFontHandle(fontScale).Push(),
        };
    }

    private IFontHandle GetOrCreateAxisFontHandle(float scale)
        => axisFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Axis, 14f * scale));

    private IFontHandle GetOrCreateJupiterFontHandle(float scale)
        => jupiterFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Jupiter, 16f * scale));

    private IFontHandle GetOrCreateTrumpGothicFontHandle(float scale)
        => trumpGothicFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.TrumpGothic, 23f * scale));

    /// <summary>
    /// Builds the display string for an alert, handling countdown and cast bar display.
    /// </summary>
    private static string BuildDisplayText(CactbotAlert alert)
    {
        if (alert.CountdownEndTime.HasValue)
        {
            var remaining = (alert.CountdownEndTime.Value - DateTime.UtcNow).TotalSeconds;
            return remaining > 0 ? $"Engage in {remaining:F1}s!" : "Engage!";
        }
        else if (alert.CastEndTime.HasValue)
        {
            var remaining = (alert.CastEndTime.Value - DateTime.UtcNow).TotalSeconds;
            return remaining > 0
                ? $"{alert.Text} ({remaining:F1}s)"
                : alert.Text;
        }
        else
        {
            return alert.Text;
        }
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> to fit within <paramref name="wrapWidth"/>
    /// pixels, returning a list of individual lines. Each line is then centered
    /// independently by the caller.
    /// </summary>
    internal static List<string> WrapTextToWidth(string text, float wrapWidth, float lineHeight)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        var words = text.Split(' ');
        var currentLine = string.Empty;

        foreach (var word in words)
        {
            var testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
            var testSize = ImGui.CalcTextSize(testLine);

            if (testSize.X > wrapWidth && currentLine.Length > 0)
            {
                lines.Add(currentLine);
                currentLine = word;
            }
            else
            {
                currentLine = testLine;
            }
        }

        if (currentLine.Length > 0)
            lines.Add(currentLine);

        // Ensure at least one entry if text was empty after trimming
        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
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

    private static IEnumerable<CactbotAlert> GetPreviewAlerts()
    {
        yield return new CactbotAlert
        {
            Text = "Preview: Stack for raidwide!",
            Duration = 9999f,
        };

        yield return new CactbotAlert
        {
            Text = "Preview: Move to safe spot.",
            Duration = 9999f,
        };
    }
}
