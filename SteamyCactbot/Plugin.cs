using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using CactbotUI.Services;
using System.Diagnostics;
using CactbotUI.Windows;

namespace CactbotUI;

/// <summary>
/// Entry point for the Cactbot Alert Overlay plugin.
///
/// Responsibilities:
///   - Inject Dalamud services
///   - Start <see cref="WebSocketService"/> on a background thread
///   - Register ImGui windows with Dalamud's <see cref="WindowSystem"/>
///   - Handle slash commands
///   - Clean up everything on unload
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // -----------------------------------------------------------------------
    // Dalamud service injection
    // Dalamud populates these via [PluginService] before the constructor runs
    // -----------------------------------------------------------------------
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider  { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager   { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState      { get; private set; } = null!;
    [PluginService] internal static IPlayerState            PlayerState      { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager      { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log              { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui          { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework        { get; private set; } = null!;

    // /cactbot       — toggle move mode
    // /cactbot config — open settings
    private const string CommandName = "/cactbot";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("CactbotPlugin");

    // -----------------------------------------------------------------------
    // Plugin-owned objects
    // -----------------------------------------------------------------------
    private readonly WebSocketService  wsService;
    private readonly RelayHttpService  relayService;
    private readonly BrowserService   browserService;
    private          ConfigWindow      ConfigWindow  { get; init; }
    private          OverlayWindow     OverlayWindow { get; init; }

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------
    public Plugin()
    {
        // Load or create configuration from Dalamud's config storage
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Start the WebSocket service — connects and listens on a background Task
        wsService = new WebSocketService(Log, Configuration);

        // Start the relay HTTP server — serves raidboss-user.js to Cactbot
        var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        relayService   = new RelayHttpService(Log, pluginDir);

        // Launch headless browser pointing at the local proxied Cactbot overlay
        browserService = new BrowserService(Log, pluginDir, relayService.OverlayUrl);

        // Create windows — OverlayWindow must exist before ConfigWindow
        // so ConfigWindow can hold a reference to it
        OverlayWindow = new OverlayWindow(this, wsService);
        ConfigWindow  = new ConfigWindow(this, wsService, OverlayWindow, relayService, browserService);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(OverlayWindow);

        // The text overlay should always remain visible.
        OverlayWindow.IsOpen = true;

        // Register slash command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle move mode for the Cactbot text anchor. '/cactbot config' opens settings."
        });

        // Hook into Dalamud's UI draw pipeline
        PluginInterface.UiBuilder.Draw        += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;
        Framework.Update                       += OnFrameworkUpdate;

        Log.Information("[CactbotPlugin] Plugin loaded.");
    }

    // -----------------------------------------------------------------------
    // Disposal — unregister everything to prevent leaks on reload
    // -----------------------------------------------------------------------
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw        -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;
        Framework.Update                       -= OnFrameworkUpdate;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        OverlayWindow.Dispose();

        // Cancels the background task and closes the WebSocket gracefully
        wsService.Dispose();
        relayService.Dispose();
        browserService.Dispose();

        CommandManager.RemoveHandler(CommandName);

        Log.Information("[CactbotPlugin] Plugin unloaded.");
    }

    // -----------------------------------------------------------------------
    // Command handler
    // -----------------------------------------------------------------------
    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", System.StringComparison.OrdinalIgnoreCase))
            ToggleConfigUi();
        else
            ToggleMainUi();
    }

    // -----------------------------------------------------------------------
    // UI toggle helpers (also wired to plugin-installer buttons)
    // -----------------------------------------------------------------------
    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void ToggleMainUi()
    {
        OverlayWindow.ToggleMoveMode();
        OverlayWindow.IsOpen = true;
    }

    // -----------------------------------------------------------------------
    // Framework update — drain chat queue on the game main thread
    // -----------------------------------------------------------------------
    private void OnFrameworkUpdate(IFramework framework)
    {
        while (wsService.TryDequeueChat(out var msg))
            ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
            {
                Type    = Dalamud.Game.Text.XivChatType.Notice,
                Message = msg
            });
    }
}

