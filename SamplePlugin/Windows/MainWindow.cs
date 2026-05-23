using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CactbotUI.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Cactbot Overlay###Main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.80f, 0.80f, 1.00f, 1f), "Cactbot Raidboss Overlay");
        ImGui.Spacing();
        ImGui.Text("This overlay displays alerts from the Cactbot raidboss module connected via OverlayPlugin WebSocket (ws://127.0.0.1:10501/ws).");
        ImGui.Spacing();
        if (ImGui.Button("Open Settings"))
            plugin.ToggleConfigUi();
    }
}
