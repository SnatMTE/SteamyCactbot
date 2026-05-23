using Dalamud.Configuration;
using System;

namespace CactbotUI;

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

    // -----------------------------------------------------------------------
    // Persistence helper
    // -----------------------------------------------------------------------

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
