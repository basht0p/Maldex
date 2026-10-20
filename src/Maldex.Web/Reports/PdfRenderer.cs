using Microsoft.Playwright;

namespace Maldex.Web.Reports;

/// <summary>
/// Prints report HTML to PDF with headless Chromium (Microsoft.Playwright). JavaScript is off and the context is offline, so the
/// page can neither run script nor fetch anything. Install the browser once with <c>./build.sh setup</c>.
/// </summary>
public sealed class PdfRenderer(IConfiguration configuration) : IAsyncDisposable
{
    private const int MaxConcurrentRenders = 2;

    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRenders);
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private IPlaywright? _playwright;

    public async Task<byte[]> RenderAsync(string html, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            return await RenderCoreAsync(html, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
        {
            // Cancelling closes the browser under Playwright, which surfaces as its own exceptions (even while disposing).
            throw new OperationCanceledException(ct);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<byte[]> RenderCoreAsync(string html, CancellationToken ct)
    {
        var playwright = await GetPlaywrightAsync(ct);
        var launch = new BrowserTypeLaunchOptions { Headless = true };
        if (configuration["Maldex:Pdf:ChromiumPath"] is { Length: > 0 } path) launch.ExecutablePath = path;

        await using var browser = await playwright.Chromium.LaunchAsync(launch);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false, Offline = true });
        // Playwright calls take no token: closing the browser aborts whatever is in flight.
        await using var onCancel = ct.Register(() => _ = CloseQuietlyAsync(browser));
        var page = await context.NewPageAsync();
        await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });
        return await page.PdfAsync(new PagePdfOptions { Format = "A4", PrintBackground = true });
    }

    private static async Task CloseQuietlyAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); }
        catch (Exception) { /* already closing or closed */ }
    }

    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken ct)
    {
        if (_playwright is { } existing) return existing;
        await _initGate.WaitAsync(ct);
        try
        {
            return _playwright ??= await Playwright.CreateAsync();
        }
        finally
        {
            _initGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _playwright?.Dispose();
        _concurrency.Dispose();
        _initGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
