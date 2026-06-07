using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PuppeteerSharp;

namespace CactbotUI.Services;

/// <summary>
/// Manages an embedded headless Chromium browser that loads the Cactbot
/// raidboss overlay in the background.
///
/// On first use, Chromium is downloaded once to <c>{pluginDir}/chromium/</c>
/// (~150 MB).  Subsequent starts reuse the cached copy.  No external browser
/// installation is required - works on Windows and Steam Deck (Proton/Wine).
/// </summary>
public sealed class BrowserService : IDisposable
{
    // -----------------------------------------------------------------------
    // State machine
    // -----------------------------------------------------------------------
    public enum BrowserState { Idle, Downloading, Launching, Running, Error }

    /// <summary>Fires whenever the browser state changes.</summary>
    public event Action<BrowserState>? StateChanged;

    public BrowserState State
    {
        get => field;
        private set
        {
            field = value;
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

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------
    private readonly IPluginLog log;
    private readonly string     overlayUrl;
    private readonly string     chromiumPath;
    private IBrowser?           browser;
    private CancellationTokenSource cts = new();
    private bool                disposed;

    public BrowserService(IPluginLog log, string pluginDirectory, string overlayUrl)
    {
        this.log          = log;
        this.overlayUrl   = overlayUrl;
        this.chromiumPath = Path.Combine(pluginDirectory, "chromium");
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
            log.Information("[CactbotPlugin] Checking embedded Chromium…");

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

            log.Information("[CactbotPlugin] Ensuring Chromium is present (~150 MB, downloaded once)…");
            var revisionInfo = await fetcher.DownloadAsync();
            var executablePath = revisionInfo.GetExecutablePath();
            log.Information($"[CactbotPlugin] Chromium ready: {executablePath}");

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
            // 3. Navigate to Cactbot overlay
            // ------------------------------------------------------------------
            var page = await browser.NewPageAsync();
            await page.GoToAsync(overlayUrl);

            State = BrowserState.Running;
            log.Information($"[CactbotPlugin] Embedded Chromium running → {overlayUrl}");
        }
        catch (OperationCanceledException)
        {
            State = BrowserState.Idle;
        }
        catch (Exception ex)
        {
            State = BrowserState.Error;
            log.Error($"[CactbotPlugin] BrowserService error: {ex}");
        }
    }

    private async Task StopBrowserAsync()
    {
        try
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
                browser.Dispose();
                browser = null;
            }
        }
        catch { /* best-effort */ }
        State = BrowserState.Idle;
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
        log.Debug("[CactbotPlugin] BrowserService disposed.");
    }
}
