using Dalamud.Configuration;
using System;
using System.Numerics;

namespace CactbotUI;

public enum AlertFontPreset
{
    FfxivAxis = 0,
    DalamudDefault = 1,
    DalamudMono = 2,
    FfxivJupiter = 3,
    FfxivTrumpGothic = 4,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // -----------------------------------------------------------------------
    // Config window
    // -----------------------------------------------------------------------

    /// <summary>Whether the configuration window itself can be dragged.</summary>
    public bool IsConfigWindowMovable { get; set; } = true;

    // -----------------------------------------------------------------------
    // Overlay visibility and position
    // -----------------------------------------------------------------------

    /// <summary>Whether the raidboss alert overlay is currently visible.</summary>
    public bool OverlayVisible { get; set; } = true;

    /// <summary>Screen X position of the overlay window (pixels).</summary>
    public float OverlayX { get; set; } = 100f;

    /// <summary>Screen Y position of the overlay window (pixels).</summary>
    public float OverlayY { get; set; } = 100f;

    /// <summary>When true the overlay cannot be dragged; position is read-only.</summary>
    public bool LockOverlayPosition { get; set; } = false;

    /// <summary>Width of the overlay box in pixels.</summary>
    public float OverlayWidth { get; set; } = 500f;

    /// <summary>Height of the overlay box in pixels.</summary>
    public float OverlayHeight { get; set; } = 150f;

    // -----------------------------------------------------------------------
    // Display tweaks
    // -----------------------------------------------------------------------

    /// <summary>Maximum number of alerts shown simultaneously (1–10).</summary>
    public int MaxVisibleAlerts { get; set; } = 5;

    // -----------------------------------------------------------------------
    // Chat output
    // -----------------------------------------------------------------------

    /// <summary>When true, each alert is also printed to the local chat log as an Announcement.</summary>
    public bool OutputToChatAnnouncement { get; set; } = false;

    /// <summary>
    /// ImGui font scale applied to alert text.
    /// 1.0 = default size, 1.5 = 50 % larger, etc.
    /// </summary>
    public float AlertFontScale { get; set; } = 1.2f;

    /// <summary>
    /// Font family preset used for alert text.
    /// Defaults to the game's Axis font style.
    /// </summary>
    public AlertFontPreset AlertFontPreset { get; set; } = AlertFontPreset.FfxivAxis;

    // -----------------------------------------------------------------------
    // Text appearance
    // -----------------------------------------------------------------------

    /// <summary>Custom text color override for alert text (RGBA).</summary>
    public Vector4 AlertTextColor { get; set; } = new(1.00f, 1.00f, 1.00f, 1f);

    /// <summary>When true, uses <see cref="AlertTextColor"/> instead of per-type colors.</summary>
    public bool UseCustomAlertColor { get; set; } = false;

    /// <summary>When true, draws a dark outline behind alert text for readability.</summary>
    public bool AlertTextOutline { get; set; } = false;

    /// <summary>Color of the text outline (RGBA).</summary>
    public Vector4 AlertOutlineColor { get; set; } = new(0.00f, 0.00f, 0.00f, 1f);

    // -----------------------------------------------------------------------
    // Persistence helper
    // -----------------------------------------------------------------------

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
