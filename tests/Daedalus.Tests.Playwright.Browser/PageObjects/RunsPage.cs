using System.Text.RegularExpressions;

namespace Daedalus.Tests.Playwright.Browser.PageObjects;

/// <summary>The Runs list at <c>/runs</c>, and the awaiting-approval count in the menu beside it.</summary>
public sealed class RunsPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    /// <summary>The grid's data rows.</summary>
    public ILocator Rows => _page.Locator("[data-testid='runs-grid'] .rz-data-grid-data tbody tr");

    /// <summary>Each row's link to its run, in row order.</summary>
    public ILocator RowLinks => Rows.Locator("a[href^='runs/']");

    /// <summary>The status filter's drop-down.</summary>
    public ILocator StatusFilter => _page.Locator("[data-testid='runs-status-filter']");

    /// <summary>The grid's header cell that reads exactly <paramref name="title"/>.</summary>
    public ILocator Header(string title) =>
        _page.Locator("[data-testid='runs-grid'] thead th").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new Regex($@"^\s*{Regex.Escape(title)}\s*$"),
        });

    /// <summary>The menu's count of runs awaiting approval.</summary>
    public ILocator AwaitingCount => _page.Locator("[data-testid='runs-awaiting-count']");

    public async Task NavigateAsync() => await NavigateToAsync("/runs").ConfigureAwait(false);

    /// <summary>The row whose link opens run <paramref name="id"/>.</summary>
    public ILocator RowFor(Guid id) =>
        Rows.Filter(new LocatorFilterOptions { Has = _page.Locator($"a[href='runs/{id}']") });

    /// <summary>Chooses <paramref name="status"/> in the status filter.</summary>
    public async Task FilterByStatusAsync(string status)
    {
        await StatusFilter.ClickAsync().ConfigureAwait(false);
        await _page.Locator(".rz-dropdown-panel .rz-dropdown-item", new PageLocatorOptions { HasText = status })
            .First.ClickAsync().ConfigureAwait(false);
    }
}
