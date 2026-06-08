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
/// Transparent, always-on-top overlay window that renders upcoming encounter
/// timeline entries (boss abilities with countdown timers) stacked vertically.
///
/// Design goals:
///   - Zero allocations per frame during normal operation
///   - Drag-to-reposition with position persisted to <see cref="Configuration"/>
///   - Shows upcoming mechanics sorted by time remaining
/// </summary>
public class TimelineOverlayWindow : Window, IDisposable
{
    // Default colour for timeline entries
    private static readonly Vector4 DefaultTimelineColor = new(0.60f, 0.80f, 1.00f, 1f); // light blue

    private const float BoxPadding = 12f;

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly Plugin            plugin;
    private readonly WebSocketService  wsService;
    private IFontHandle? axisFontHandle;
    private IFontHandle? jupiterFontHandle;
    private IFontHandle? trumpGothicFontHandle;

    private float           lastFontScale;
    private AlertFontPreset lastFontPreset;

    // Re-used every frame to avoid per-frame heap allocation
    private readonly List<TimelineEntry> frameEntries = new();

    private bool positionInitialised;
    private bool moveMode;

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------

    public TimelineOverlayWindow(Plugin plugin, WebSocketService wsService)
        : base("##CactBridgeTimeline",
               ImGuiWindowFlags.NoTitleBar        |
               ImGuiWindowFlags.NoScrollbar       |
               ImGuiWindowFlags.NoScrollWithMouse |
               ImGuiWindowFlags.NoSavedSettings   |
               ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin    = plugin;
        this.wsService = wsService;

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
        IsOpen = cfg.EnableTimelineOverlay;

        // Rebuild font handles when settings change
        if (MathF.Abs(cfg.TimelineFontScale - lastFontScale) > 0.01f || cfg.TimelineFontPreset != lastFontPreset)
        {
            InvalidateFontHandles();
            lastFontScale = cfg.TimelineFontScale;
            lastFontPreset = cfg.TimelineFontPreset;
        }

        // Apply saved position on first frame
        if (!positionInitialised)
        {
            if (MathF.Abs(cfg.TimelineX - 100f) < 0.5f && MathF.Abs(cfg.TimelineY - 300f) < 0.5f)
            {
                var size = ImGui.GetIO().DisplaySize;
                ImGui.SetNextWindowPos(new Vector2(size.X * 0.5f, size.Y * 0.5f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
            }
            else
            {
                ImGui.SetNextWindowPos(new Vector2(cfg.TimelineX, cfg.TimelineY), ImGuiCond.Always);
            }
            positionInitialised = true;
        }

        ImGui.SetNextWindowSize(new Vector2(cfg.TimelineWidth, cfg.TimelineHeight), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(moveMode ? 0.15f : 0.00f);

        if (!moveMode || cfg.LockTimelinePosition)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~ImGuiWindowFlags.NoMove;

        if (moveMode)
            Flags &= ~ImGuiWindowFlags.NoInputs;
        else
            Flags |= ImGuiWindowFlags.NoInputs;
    }

    public override void OnClose()
    {
        positionInitialised = false;
    }

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
        using var fontPush = PushConfiguredTimelineFont(cfg.TimelineFontPreset, cfg.TimelineFontScale);

        // Collect timeline entries
        frameEntries.Clear();
        frameEntries.AddRange(wsService.GetTimelineEntries(cfg.MaxVisibleTimelineEntries, cfg.TimelineLookAhead));

        var boxSize = ImGui.GetWindowSize();
        var windowPos = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();
        var fontSize = ImGui.GetFontSize();

        const float barHeight    = 28f;
        const float barGap       = 4f;
        const float barMarginX   = 8f;
        const float borderRadius = 3f;
        const float textPadX     = 8f;

        // Nothing to show – render a subtle drag-handle
        if (frameEntries.Count == 0)
        {
            if (moveMode)
            {
                ImGui.SetCursorPos(new Vector2(BoxPadding, BoxPadding));
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 0.75f), "Timeline: drag to reposition. Use /cactbridge to finish.");
            }
            return;
        }

        // Calculate total height and vertical offset to centre the block
        var totalHeight = frameEntries.Count * barHeight + (frameEntries.Count - 1) * barGap;
        var startY = Math.Max(BoxPadding, (boxSize.Y - totalHeight) * 0.5f);

        var outlineU32 = cfg.TimelineTextOutline
            ? ImGui.ColorConvertFloat4ToU32(cfg.TimelineOutlineColor)
            : 0u;

        for (int i = 0; i < frameEntries.Count; i++)
        {
            var entry = frameEntries[i];

            // Bar rectangle coordinates
            var barX = barMarginX;
            var barY = startY + i * (barHeight + barGap);
            var barW = boxSize.X - barMarginX * 2f;

            // Fill ratio: 0 = just appeared, 1 = about to expire
            var initTime = Math.Max(entry.InitialTimeRemaining, 0.1);
            var fillRatio = (float)Math.Clamp(1.0 - (entry.TimeRemaining / initTime), 0.0, 1.0);

            // Background rectangle (dark, rounded)
            drawList.AddRectFilled(
                windowPos + new Vector2(barX, barY),
                windowPos + new Vector2(boxSize.X - barMarginX, barY + barHeight),
                0x40000000, borderRadius);

            // Fill bar width
            var fillW = (float)Math.Max(barW * fillRatio, 0.0);
            if (fillW > 0f)
            {
                // Colour based on time remaining to simulate web version urgency
                uint fillColor;
                var timeRem = (float)entry.TimeRemaining;
                if (timeRem <= 0)
                    fillColor = 0xCCFF4444u;  // red (past due)
                else if (timeRem <= 3)
                    fillColor = 0xCCFF6644u;  // orange-red
                else if (timeRem <= 6)
                    fillColor = 0xCCFFAA44u;  // orange
                else if (timeRem <= 10)
                    fillColor = 0xCCCCCC44u;  // yellow-green
                else
                    fillColor = 0xCC44CC44u;  // green

                if (cfg.UseCustomTimelineColor)
                {
                    var custom = cfg.TimelineTextColor;
                    fillColor = ImGui.ColorConvertFloat4ToU32(new Vector4(
                        custom.X * (1f - fillRatio * 0.5f),
                        custom.Y * (1f - fillRatio * 0.3f),
                        custom.Z,
                        custom.W));
                }

                drawList.AddRectFilled(
                    windowPos + new Vector2(barX, barY),
                    windowPos + new Vector2(barX + fillW, barY + barHeight),
                    fillColor, borderRadius,
                    fillRatio >= 0.99f ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);
            }

            // --- Text overlays ---

            // Ability name on the left
            var textY = barY + (barHeight - fontSize) * 0.5f;
            var namePos = windowPos + new Vector2(barX + textPadX, textY);

            if (cfg.TimelineTextOutline)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            drawList.AddText(namePos + new Vector2(dx, dy), outlineU32, entry.Text);
            }
            drawList.AddText(namePos, 0xFFFFFFFF, entry.Text);

            // Time remaining on the right
            var timeText = entry.TimeRemaining > 0 ? $"{entry.TimeRemaining:F1}s" : "NOW";
            var timeTextSize = ImGui.CalcTextSize(timeText);
            var timePos = windowPos + new Vector2(
                boxSize.X - barMarginX - textPadX - timeTextSize.X, textY);

            if (cfg.TimelineTextOutline)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            drawList.AddText(timePos + new Vector2(dx, dy), outlineU32, timeText);
            }
            drawList.AddText(timePos, 0xFFFFFFFF, timeText);
        }

        // Persist position and size
        var pos = ImGui.GetWindowPos();
        if (MathF.Abs(pos.X - cfg.TimelineX) > 0.5f || MathF.Abs(pos.Y - cfg.TimelineY) > 0.5f)
        {
            cfg.TimelineX = pos.X;
            cfg.TimelineY = pos.Y;
            cfg.Save();
        }

        var currentSize = ImGui.GetWindowSize();
        if (MathF.Abs(currentSize.X - cfg.TimelineWidth) > 0.5f || MathF.Abs(currentSize.Y - cfg.TimelineHeight) > 0.5f)
        {
            cfg.TimelineWidth = currentSize.X;
            cfg.TimelineHeight = currentSize.Y;
            cfg.Save();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void InvalidateFontHandles()
    {
        axisFontHandle?.Dispose();
        axisFontHandle = null;
        jupiterFontHandle?.Dispose();
        jupiterFontHandle = null;
        trumpGothicFontHandle?.Dispose();
        trumpGothicFontHandle = null;
    }

    public IDisposable? PushConfiguredTimelineFont(AlertFontPreset preset, float fontScale)
    {
        var ui = Plugin.PluginInterface.UiBuilder;
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
    /// Builds the display string for a timeline entry.
    /// </summary>
    private static string BuildDisplayText(TimelineEntry entry)
    {
        if (entry.TimeRemaining > 0)
            return $"{entry.Text} ({entry.TimeRemaining:F1}s)";
        return $"{entry.Text} (NOW)";
    }

    /// <summary>
    /// Word-wraps text to fit within available width.
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

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }
}
