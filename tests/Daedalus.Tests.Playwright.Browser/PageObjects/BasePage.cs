namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public abstract class BasePage(IPage page, Uri baseUrl)
{
    protected readonly Uri _baseUrl = baseUrl;
    protected readonly IPage _page = page;

    public async Task NavigateToAsync(string path)
    {
        var url = new Uri(_baseUrl, path);
        await _page.GotoAsync(url.ToString()).ConfigureAwait(false);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
    }

    public string GetCurrentUrl() => _page.Url;

    public async Task WaitForElementAsync(string selector, int timeoutMs = 5000)
    {
        await _page.Locator(selector).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs
        }).ConfigureAwait(false);
    }

    public async Task WaitForElementHiddenAsync(string selector, int timeoutMs = 5000)
    {
        await _page.Locator(selector).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = timeoutMs
        }).ConfigureAwait(false);
    }

    public async Task FillAsync(string selector, string text) =>
        await _page.Locator(selector).FillAsync(text).ConfigureAwait(false);

    public async Task ClickAsync(string selector) => await _page.Locator(selector).ClickAsync().ConfigureAwait(false);

    public async Task<string> GetTextAsync(string selector) =>
        await _page.Locator(selector).TextContentAsync().ConfigureAwait(false) ?? string.Empty;

    public async Task<string> GetInnerHtmlAsync(string selector) =>
        await _page.Locator(selector).InnerHTMLAsync().ConfigureAwait(false) ?? string.Empty;

    public async Task<bool> IsVisibleAsync(string selector) =>
        await _page.Locator(selector).IsVisibleAsync().ConfigureAwait(false);

    public async Task<bool> IsHiddenAsync(string selector) =>
        await _page.Locator(selector).IsHiddenAsync().ConfigureAwait(false);

    public async Task<bool> IsEnabledAsync(string selector) =>
        await _page.Locator(selector).IsEnabledAsync().ConfigureAwait(false);

    public async Task<bool> IsCheckedAsync(string selector) =>
        await _page.Locator(selector).IsCheckedAsync().ConfigureAwait(false);

    public async Task<string> GetInputValueAsync(string selector) =>
        await _page.Locator(selector).InputValueAsync().ConfigureAwait(false) ?? string.Empty;

    public async Task SelectOptionAsync(string selector, string value) =>
        await _page.Locator(selector).SelectOptionAsync(value).ConfigureAwait(false);

    public async Task HoverAsync(string selector) => await _page.Locator(selector).HoverAsync().ConfigureAwait(false);

    public async Task PressKeyAsync(string key) => await _page.PressAsync("body", key).ConfigureAwait(false);

    public async Task WaitForNavigationAsync(Func<Task> action)
    {
        var navigationTask = _page.WaitForNavigationAsync();
        await action().ConfigureAwait(false);
        await navigationTask.ConfigureAwait(false);
    }

    public async Task TakeScreenshotAsync(string name, bool fullPage = true)
    {
        var screenshotsDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshots");
        Directory.CreateDirectory(screenshotsDir);
        var fileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        var filePath = Path.Combine(screenshotsDir, fileName);
        await _page.ScreenshotAsync(new PageScreenshotOptions { Path = filePath, FullPage = fullPage })
            .ConfigureAwait(false);
    }

    public async Task WaitAsync(int milliseconds) => await Task.Delay(milliseconds).ConfigureAwait(false);
}
