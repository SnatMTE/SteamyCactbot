using System;
using System.Numerics;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using CactbotUI.Services;

namespace CactbotUI.Windows;

/// <summary>
/// Configuration window for the Cactbot alert overlay.
/// Lets the user toggle visibility, adjust display settings, and reposition
/// the overlay. All changes are persisted immediately via
/// <see cref="Configuration.Save"/>.
/// </summary>
public class ConfigWindow : Window, IDisposable
{
    private static readonly string[] FontPresetLabels =
    {
        "FFXIV Axis (default)",
        "Dalamud Default",
        "Dalamud Monospace",
        "FFXIV Jupiter",
        "FFXIV Trump Gothic",
    };

    private readonly Configuration    configuration;
    private readonly WebSocketService wsService;
    private readonly OverlayWindow    overlayWindow;
    private readonly RelayHttpService relayService;
    private readonly BrowserService   browserService;

    // Constant window ID - the title can change without breaking ImGui identity
    public ConfigWindow(Plugin plugin, WebSocketService wsService, OverlayWindow overlayWindow, RelayHttpService relayService, BrowserService browserService)
        : base("Cactbot Overlay Settings###CactbotConfig")
    {
        Flags = ImGuiWindowFlags.NoResize    |
                ImGuiWindowFlags.NoCollapse;

        Size          = new Vector2(620, 720);
        SizeCondition = ImGuiCond.Always;

        configuration      = plugin.Configuration;
        this.wsService     = wsService;
        this.overlayWindow = overlayWindow;
        this.relayService  = relayService;
        this.browserService = browserService;
    }

    public void Dispose() { }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void PreDraw()
    {
        // Allow or prevent dragging this config window based on config
        if (configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |= ImGuiWindowFlags.NoMove;
    }

    // -----------------------------------------------------------------------
    // Draw
    // -----------------------------------------------------------------------

    public override void Draw()
    {
        if (!string.IsNullOrEmpty(wsService.CurrentZone))
        {
            ImGui.Text("Zone:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.80f, 0.80f, 1.00f, 1f), wsService.CurrentZone);
            ImGui.Separator();
        }

        // ------------------------------------------------------------------
        // Background browser status
        // ------------------------------------------------------------------
        ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Background Cactbot Browser");
        ImGui.Spacing();

        if (relayService.Port <= 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Relay HTTP server failed to start (port in use?).");
        }
        else
        {
            switch (browserService.State)
            {
                case BrowserService.BrowserState.Downloading:
                    ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.20f, 1f), "\u23f3 Downloading Chromium\u2026");
                    ImGui.ProgressBar(browserService.DownloadPct / 100f, new Vector2(-1, 20), $"{browserService.DownloadPct}%");
                    ImGui.TextWrapped("One-time ~150 MB download. Alerts will begin once complete.");
                    break;
                case BrowserService.BrowserState.Launching:
                    ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.20f, 1f), "\u23f3 Launching browser\u2026");
                    break;
                case BrowserService.BrowserState.Running:
                    ImGui.TextColored(new Vector4(0.20f, 1.00f, 0.20f, 1f), $"\u25cf {browserService.Status}");
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), relayService.OverlayUrl);
                    break;
                case BrowserService.BrowserState.Error:
                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"\u25cf {browserService.Status}");
                    break;
                default:
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "\u25cf Idle");
                    break;
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Restart Browser"))
            browserService.Restart();
        ImGui.SameLine();
        if (ImGui.Button("Copy Overlay URL"))
            ImGui.SetClipboardText(relayService.OverlayUrl);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy URL to open the overlay in your own browser if needed.");

        ImGui.Separator();

        // ------------------------------------------------------------------
        // Move mode
        // ------------------------------------------------------------------
        var moveMode = overlayWindow.IsMoveMode;
        if (ImGui.Checkbox("Move mode (/cactbot)", ref moveMode))
        {
            overlayWindow.SetMoveMode(moveMode);
            overlayWindow.IsOpen = true;
        }

        // ------------------------------------------------------------------
        // Position lock
        // ------------------------------------------------------------------
        var locked = configuration.LockOverlayPosition;
        if (ImGui.Checkbox("Lock overlay position", ref locked))
        {
            configuration.LockOverlayPosition = locked;
            configuration.Save();
        }

        // ------------------------------------------------------------------
        // Max visible alerts slider
        // ------------------------------------------------------------------
        var maxAlerts = configuration.MaxVisibleAlerts;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderInt("Max visible alerts", ref maxAlerts, 1, 10))
        {
            configuration.MaxVisibleAlerts = maxAlerts;
            configuration.Save();
        }

        // ------------------------------------------------------------------
        // Font scale slider
        // ------------------------------------------------------------------
        var fontScale = configuration.AlertFontScale;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Font scale", ref fontScale, 0.5f, 3.0f, "%.1fx"))
        {
            configuration.AlertFontScale = fontScale;
            configuration.Save();
        }

        // Font family selection
        var fontPreset = (int)configuration.AlertFontPreset;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.Combo("Alert font", ref fontPreset, FontPresetLabels, FontPresetLabels.Length))
        {
            configuration.AlertFontPreset = (AlertFontPreset)fontPreset;
            configuration.Save();
        }

        // Text thickness slider
        var fontThickness = configuration.AlertFontThickness;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Font thickness", ref fontThickness, 1.0f, 3.0f, "%.1fx"))
        {
            configuration.AlertFontThickness = fontThickness;
            configuration.Save();
        }

        // ------------------------------------------------------------------
        // Text colour override
        // ------------------------------------------------------------------
        var useCustomColor = configuration.UseCustomAlertColor;
        if (ImGui.Checkbox("Override alert text colour", ref useCustomColor))
        {
            configuration.UseCustomAlertColor = useCustomColor;
            configuration.Save();
        }
        if (useCustomColor)
        {
            var textCol = configuration.AlertTextColor;
            if (ImGui.ColorEdit4("Text colour", ref textCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
            {
                configuration.AlertTextColor = textCol;
                configuration.Save();
            }
        }

        // ------------------------------------------------------------------
        // Text outline
        // ------------------------------------------------------------------
        var outline = configuration.AlertTextOutline;
        if (ImGui.Checkbox("Text outline", ref outline))
        {
            configuration.AlertTextOutline = outline;
            configuration.Save();
        }
        if (outline)
        {
            var outlineCol = configuration.AlertOutlineColor;
            if (ImGui.ColorEdit4("Outline colour", ref outlineCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
            {
                configuration.AlertOutlineColor = outlineCol;
                configuration.Save();
            }
        }

        ImGui.Separator();

        // ------------------------------------------------------------------
        // Position readout + reset
        // ------------------------------------------------------------------
        ImGui.Text($"Position: ({configuration.OverlayX:F0}, {configuration.OverlayY:F0})");
        ImGui.SameLine();
        if (ImGui.Button("Reset"))
        {
            configuration.OverlayX = 100f;
            configuration.OverlayY = 100f;
            overlayWindow.ResetPosition();   // re-apply saved coords next PreDraw
            configuration.Save();
        }

        ImGui.Separator();

        // ------------------------------------------------------------------
        // Overlay box size
        // ------------------------------------------------------------------
        ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Overlay Box Size");
        ImGui.Spacing();

        var boxW = configuration.OverlayWidth;
        var boxH = configuration.OverlayHeight;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.DragFloat("Width", ref boxW, 1f, 100f, 2000f, "%.0f"))
        {
            configuration.OverlayWidth = Math.Clamp(boxW, 100f, 2000f);
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        if (ImGui.DragFloat("Height", ref boxH, 1f, 50f, 1000f, "%.0f"))
        {
            configuration.OverlayHeight = Math.Clamp(boxH, 50f, 1000f);
            configuration.Save();
        }

        ImGui.Spacing();

        // ------------------------------------------------------------------
        // Preview – mirrors the overlay rendering (wrapping + centering)
        // Resize the inputs above to see how text wraps and centers.
        // ------------------------------------------------------------------
        ImGui.TextColored(new Vector4(1.00f, 0.85f, 0.10f, 1f), "Preview");
        ImGui.Spacing();

        var previewW = Math.Clamp(configuration.OverlayWidth, 100f, ImGui.GetContentRegionAvail().X);
        var previewH = Math.Min(configuration.OverlayHeight, 250f);
        ImGui.BeginChild("##PreviewBox", new Vector2(previewW, previewH), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var sampleText = "Sample Alert: Stack for raidwide!";
        var previewWrapWidth = Math.Max(1f, ImGui.GetWindowWidth() - 24f);

        using var previewFontPush = overlayWindow.PushConfiguredAlertFont(configuration.AlertFontPreset);
        var previewLineHeight = ImGui.GetFontSize() * configuration.AlertFontScale;
        ImGui.SetWindowFontScale(configuration.AlertFontScale);

        var previewLines = OverlayWindow.WrapTextToWidth(sampleText, previewWrapWidth, previewLineHeight);
        var totalPreviewHeight = previewLines.Count * previewLineHeight;
        var previewBoxSize = ImGui.GetWindowSize();
        var previewStartY = Math.Max(0f, (previewBoxSize.Y - totalPreviewHeight) * 0.5f);

        var previewDrawList = ImGui.GetWindowDrawList();
        var previewWindowPos = ImGui.GetWindowPos();
        var previewBaseColor = configuration.UseCustomAlertColor
            ? configuration.AlertTextColor
            : new Vector4(1f, 1f, 1f, 1f);
        var previewColorU32 = ImGui.ColorConvertFloat4ToU32(previewBaseColor);

        for (int l = 0; l < previewLines.Count; l++)
        {
            var lineW = ImGui.CalcTextSize(previewLines[l]).X;
            var lineX = Math.Max(0f, (previewBoxSize.X - lineW) * 0.5f);
            var lineY = previewStartY + l * previewLineHeight;
            var screenPt = previewWindowPos + new Vector2(lineX, lineY);

            if (configuration.AlertTextOutline)
            {
                var outlineU32 = ImGui.ColorConvertFloat4ToU32(configuration.AlertOutlineColor);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            previewDrawList.AddText(screenPt + new Vector2(dx, dy), outlineU32, previewLines[l]);
            }

            OverlayWindow.DrawTextWithThickness(previewDrawList, screenPt, previewColorU32, previewLines[l], configuration.AlertFontThickness);
        }

        ImGui.SetWindowFontScale(1.0f);
        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Adjust Width/Height above to see how text wraps and centers inside the box.");

        ImGui.Separator();

        // ------------------------------------------------------------------
        // Misc
        // ------------------------------------------------------------------
        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable config window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        // ------------------------------------------------------------------
        // Chat announcement output
        // ------------------------------------------------------------------
        var chatOut = configuration.OutputToChatAnnouncement;
        if (ImGui.Checkbox("Output alerts to chat as Announcement", ref chatOut))
        {
            configuration.OutputToChatAnnouncement = chatOut;
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Prints each alert to your local chat log using the Announcement channel.");

        ImGui.Separator();

        // ------------------------------------------------------------------
        // Raw WebSocket debug panel
        // Opens /xllog in Dalamud if you want persistent output.
        // All message types are also logged there at [DBG] level.
        // ------------------------------------------------------------------
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.10f, 1f), "Raw WebSocket messages (newest first)");
        ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1f), "Check /xllog for full details. Enable debug logging in /xlsettings.");

        var logLineCount = wsService.LogLineCount;
        if (logLineCount == 0)
            ImGui.TextColored(new Vector4(1.00f, 0.55f, 0.20f, 1f), "⚠ No LogLine events received - verify OverlayPlugin/IINACT is running and network parsing is enabled.");
        else
            ImGui.TextColored(new Vector4(0.20f, 1.00f, 0.20f, 1f), $"✔ {logLineCount} LogLine events received");

        ImGui.Spacing();

        var msgs = wsService.GetDebugMessages();
        if (msgs.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "(nothing received yet)");
        }
        else
        {
            using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child(
                "##DebugMsgs", new Vector2(-1, 200), true);
            if (child.Success)
            {
                foreach (var m in msgs)
                {
                    ImGui.TextUnformatted(m);
                    ImGui.Separator();
                }
            }
        }
    }
}

