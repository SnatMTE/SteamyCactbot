using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PuppeteerSharp;

namespace CactBridge.Services;

/// <summary>
/// Manages an embedded headless Chromium browser that loads the Cactbot
/// raidboss overlay in the background. Supports a primary (alerts) page
/// and an optional secondary (timeline) page sharing the same browser process.
///
/// On first use, Chromium is downloaded once to <c>%APPDATA%/CactBridge/chromium/</c>
/// (~150 MB).  Subsequent starts reuse the cached copy.  The persistent
/// location survives Dalamud plugin updates that wipe the plugin directory.
/// No external browser installation is required - works on Windows and Steam
/// Deck (Proton/Wine).
/// </summary>
public sealed class BrowserService : IDisposable
{
    // -----------------------------------------------------------------------
    // State machine
    // -----------------------------------------------------------------------
    public enum BrowserState { Idle, Downloading, Launching, Running, Error }

    /// <summary>Fires whenever the browser state changes.</summary>
    public event Action<BrowserState>? StateChanged;

    private BrowserState state = BrowserState.Idle;

    public BrowserState State
    {
        get => state;
        private set
        {
            state = value;
            StateChanged?.Invoke(value);
        }
    }
    public int          DownloadPct    { get; private set; }   // 0-100 while downloading (not available in this version)
    public bool         IsRunning      => State == BrowserState.Running;

    public string Status => State switch
    {
        BrowserState.Idle        => "Idle",
        BrowserState.Downloading => $"Downloading Chromium… {DownloadPct}%",
        BrowserState.Launching   => "Launching…",
        BrowserState.Running     => "Running",
        BrowserState.Error       => "Error - check /xllog",
        _                        => "Unknown",
    };

    /// <summary>Status string for the timeline page specifically.</summary>
    public string TimelineStatus { get; private set; } = "Idle";

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------
    private readonly IPluginLog log;
    private readonly string     overlayUrl;
    private readonly string     timelineUrl;
    private readonly string     chromiumPath;
    private IBrowser?           browser;
    private IPage?              alertsPage;
    private IPage?              timelinePage;
    private CancellationTokenSource cts = new();
    private bool                disposed;

    public BrowserService(IPluginLog log, string pluginDirectory, string overlayUrl, string timelineUrl)
    {
        this.log          = log;
        this.overlayUrl   = overlayUrl;
        this.timelineUrl  = timelineUrl;
        // Store Chromium in %APPDATA%/CactBridge so it survives plugin updates
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        this.chromiumPath = Path.Combine(appData, "CactBridge", "chromium");
        _ = Task.Run(StartAsync);
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public void Restart()
    {
        cts.Cancel();
        cts = new CancellationTokenSource();
        _ = StopBrowserAsync().ContinueWith(_ => StartAsync());
    }

    /// <summary>Restart only the timeline page (navigate to the timeline URL).</summary>
    public void RestartTimeline()
    {
        var page = timelinePage;
        if (page != null && !page.IsClosed)
            _ = page.GoToAsync(timelineUrl).ContinueWith(t =>
            {
                if (t.Exception != null)
                    log.Warning($"[CactBridge] Failed to reload timeline page: {t.Exception.Message}");
            });
    }

    // -----------------------------------------------------------------------
    // Internal lifecycle
    // -----------------------------------------------------------------------

    private async Task StartAsync()
    {
        var ct = cts.Token;
        try
        {
            // ------------------------------------------------------------------
            // 1. Ensure Chromium is present (downloads ~150 MB on first run)
            // ------------------------------------------------------------------
            State = BrowserState.Downloading;
            log.Information("[CactBridge] Checking embedded Chromium…");

            var fetcher = new BrowserFetcher(new BrowserFetcherOptions
            {
                Path = chromiumPath,
                // Stream the download manually so we can track progress
                CustomFileDownload = async (url, filePath) =>
                {
                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromMinutes(20);
                    using var response = await http.GetAsync(
                        url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;
                    using var downloadStream = await response.Content.ReadAsStreamAsync(ct);
                    using var fileStream     = File.Create(filePath);

                    var buf  = new byte[65536];
                    long done = 0;
                    int  read;
                    while ((read = await downloadStream.ReadAsync(buf, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        if (total > 0)
                            DownloadPct = (int)(done * 100L / total);
                    }
                },
            });

            log.Information("[CactBridge] Ensuring Chromium is present (~150 MB, downloaded once)…");
            var revisionInfo = await fetcher.DownloadAsync();
            var executablePath = revisionInfo.GetExecutablePath();
            log.Information($"[CactBridge] Chromium ready: {executablePath}");

            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // 2. Launch headless browser
            // ------------------------------------------------------------------
            State = BrowserState.Launching;

            browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless         = true,
                ExecutablePath   = executablePath,
                Args             = new[]
                {
                    "--no-sandbox",
                    "--disable-gpu",
                    "--disable-dev-shm-usage",
                    "--log-level=3",
                },
            });

            if (ct.IsCancellationRequested) { await StopBrowserAsync(); return; }

            // ------------------------------------------------------------------
            // 3. Navigate to Cactbot overlay (alerts page)
            // ------------------------------------------------------------------
            alertsPage = await browser.NewPageAsync();
            await alertsPage.GoToAsync(overlayUrl);
            log.Information($"[CactBridge] Alerts page → {overlayUrl}");

            if (ct.IsCancellationRequested) { await StopBrowserAsync(); return; }

            // ------------------------------------------------------------------
            // 4. Navigate to Cactbot overlay (timeline page)
            // ------------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(timelineUrl))
            {
                TimelineStatus = "Launching…";
                timelinePage = await browser.NewPageAsync();
                await timelinePage.GoToAsync(timelineUrl);
                TimelineStatus = "Running";
                log.Information($"[CactBridge] Timeline page → {timelineUrl}");
            }
            else
            {
                TimelineStatus = "Not configured";
            }

            State = BrowserState.Running;
        }
        catch (OperationCanceledException)
        {
            State = BrowserState.Idle;
            TimelineStatus = "Idle";
        }
        catch (Exception ex)
        {
            State = BrowserState.Error;
            TimelineStatus = "Error";
            log.Error($"[CactBridge] BrowserService error: {ex}");
        }
    }

    private async Task StopBrowserAsync()
    {
        try
        {
            if (timelinePage is not null)
            {
                await timelinePage.CloseAsync();
                timelinePage.Dispose();
                timelinePage = null;
            }
            if (alertsPage is not null)
            {
                await alertsPage.CloseAsync();
                alertsPage.Dispose();
                alertsPage = null;
            }
            if (browser is not null)
            {
                await browser.CloseAsync();
                browser.Dispose();
                browser = null;
            }
        }
        catch { /* best-effort */ }
        State = BrowserState.Idle;
        TimelineStatus = "Idle";
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        try { browser?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        browser?.Dispose();
        cts.Dispose();
        log.Debug("[CactBridge] BrowserService disposed.");
    }
}
