using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace CourtParser.Infrastructure.Services;

public class PageNavigationService(ILogger<PageNavigationService> logger)
{
    public async Task<IPage> InitializePageAsync(IBrowser browser)
    {
        var page = await browser.NewPageAsync();
        await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return page;
    }

    public async Task GoToUrlAsync(IPage page, string url, WaitUntilNavigation waitUntil = WaitUntilNavigation.Networkidle2)
    {
        logger.LogInformation("Переходим по URL: {Url}", url);
        await page.GoToAsync(url, waitUntil);
    }

    public async Task WaitForNavigationAsync(IPage page, NavigationOptions options)
    {
        await page.WaitForNavigationAsync(options);
    }

    public async Task<bool> WaitForSelectorAsync(IPage page, string selector, int timeoutMs = 30000)
    {
        try
        {
            await page.WaitForSelectorAsync(selector, new WaitForSelectorOptions 
            { 
                Timeout = timeoutMs 
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Селектор {Selector} не найден за {Timeout}ms", selector, timeoutMs);
            return false;
        }
    }
}