using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using CactBridge.Services;
using System.Diagnostics;
using CactBridge.Windows;

namespace CactBridge;

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

    // /cactbridge       - toggle move mode
    // /cactbridge config - open settings
    private const string CommandName = "/cactbridge";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("CactBridge");

    // -----------------------------------------------------------------------
    // Plugin-owned objects
    // -----------------------------------------------------------------------
    private readonly WebSocketService       wsService;
    private readonly RelayHttpService       relayService;
    private readonly BrowserService        browserService;
    private          ConfigWindow              ConfigWindow  { get; init; }
    private          OverlayWindow             OverlayWindow { get; init; }
    private          TimelineOverlayWindow     TimelineOverlayWindow { get; init; }
    private          DamageMeterOverlayWindow  DamageMeterOverlayWindow { get; init; }

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------
    public Plugin()
    {
        // Load or create configuration from Dalamud's config storage
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Start the WebSocket service - connects and listens on a background Task
        wsService = new WebSocketService(Log, Configuration);

        // Start the relay HTTP server - serves raidboss-user.js to Cactbot
        var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        relayService   = new RelayHttpService(Log, pluginDir);

        // Launch headless browser with both alerts and timeline pages
        browserService = new BrowserService(Log, pluginDir, relayService.OverlayUrl, relayService.TimelineOverlayUrl);

        // Installation announcements — show progress in the chat announcement channel.
        // 1. Browser download starting
        wsService.EnqueueChatMessage("Installing: Browser...");

        // 2. Subscribe to browser state changes for subsequent steps
        browserService.StateChanged += OnBrowserStateChanged;

        // Create windows - OverlayWindow must exist before ConfigWindow
        // so ConfigWindow can hold a reference to it
        OverlayWindow             = new OverlayWindow(this, wsService);
        TimelineOverlayWindow     = new TimelineOverlayWindow(this, wsService);
        DamageMeterOverlayWindow  = new DamageMeterOverlayWindow(this, wsService);
        ConfigWindow              = new ConfigWindow(this, wsService, OverlayWindow, TimelineOverlayWindow, DamageMeterOverlayWindow, relayService, browserService);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(OverlayWindow);
        WindowSystem.AddWindow(TimelineOverlayWindow);
        WindowSystem.AddWindow(DamageMeterOverlayWindow);

        // All overlays should always remain visible.
        OverlayWindow.IsOpen = true;
        TimelineOverlayWindow.IsOpen = true;
        DamageMeterOverlayWindow.IsOpen = true;

        // Register slash command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle move mode for the Cactbot text anchor. '/cactbridge config' opens settings."
        });

        // Hook into Dalamud's UI draw pipeline
        PluginInterface.UiBuilder.Draw        += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;
        Framework.Update                       += OnFrameworkUpdate;

        Log.Information("[CactBridge] Plugin loaded.");
    }

    // -----------------------------------------------------------------------
    // Browser installation announcements
    // -----------------------------------------------------------------------

    private void OnBrowserStateChanged(BrowserService.BrowserState state)
    {
        switch (state)
        {
            case BrowserService.BrowserState.Launching:
                wsService.EnqueueChatMessage("Installing: Puppet...");
                break;
            case BrowserService.BrowserState.Running:
                wsService.EnqueueChatMessage("Plugin Loaded!");
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Disposal - unregister everything to prevent leaks on reload
    // -----------------------------------------------------------------------
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw        -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;
        Framework.Update                       -= OnFrameworkUpdate;

        // Unsubscribe from browser state changes
        browserService.StateChanged -= OnBrowserStateChanged;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        OverlayWindow.Dispose();
        TimelineOverlayWindow.Dispose();
        DamageMeterOverlayWindow.Dispose();

        // Cancels the background task and closes the WebSocket gracefully
        wsService.Dispose();
        relayService.Dispose();
        browserService.Dispose();

        CommandManager.RemoveHandler(CommandName);

        Log.Information("[CactBridge] Plugin unloaded.");
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

    public bool IsConfigUiOpen => ConfigWindow.IsOpen;

    public void ToggleMainUi()
    {
        OverlayWindow.ToggleMoveMode();
        OverlayWindow.IsOpen = true;
    }

    // -----------------------------------------------------------------------
    // Framework update - drain chat queue on the game main thread
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

