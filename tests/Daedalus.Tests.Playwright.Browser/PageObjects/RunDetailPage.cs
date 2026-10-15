namespace Daedalus.Tests.Playwright.Browser.PageObjects;

/// <summary>One run at <c>/runs/{id}</c>: its header, gate, tabs and the cancel dialog.</summary>
public sealed class RunDetailPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator Header => ById("run-header");
    public ILocator Status => Header.Locator("[data-testid='run-status']");
    public ILocator Retry => ById("run-retry");
    public ILocator LastError => ById("run-last-error");
    public ILocator NotFound => ById("run-not-found");
    public ILocator Forbidden => ById("run-forbidden");
    public ILocator Error => ById("run-error");
    public ILocator Timeline => ById("run-timeline");
    public ILocator Steps => ById("run-step");
    public ILocator GatePanel => ById("gate-panel");
    public ILocator GateFindings => _page.Locator("[data-testid^='gate-drop-']");
    public ILocator Approve => ById("gate-approve");
    public ILocator Cancel => ById("gate-cancel");
    public ILocator CancelReason => _page.Locator("[data-testid='cancel-reason']");
    public ILocator CancelKeep => ById("cancel-keep");
    public ILocator CancelConfirm => ById("cancel-confirm");
    public ILocator Dialog => _page.Locator(".rz-dialog");
    public ILocator TabHeaders => _page.Locator(".run-tabs .rz-tabview-nav li");
    public ILocator Notification => _page.Locator(".rz-notification");

    /// <summary>The Failed alert, with why the run failed.</summary>
    public ILocator Failure => ById("run-failure");

    /// <summary>The Review tab's list of review visits, and its states.</summary>
    public ILocator ReviewVisits => ById("review-visits");

    /// <summary>Each review visit, newest first.</summary>
    public ILocator Visits => ById("review-visit");

    /// <summary>The alert naming the review records that could not be read.</summary>
    public ILocator ReviewVisitsError => ById("review-visits-error");

    /// <summary>The verdict badge of <paramref name="visit"/>.</summary>
    public static ILocator Verdict(ILocator visit) => visit.Locator("[data-testid='review-visit-verdict']");

    /// <summary>The button that opens or closes the <paramref name="lens"/> section of <paramref name="visit"/>.</summary>
    public static ILocator LensToggle(ILocator visit, string lens) =>
        visit.Locator("[data-testid='review-lens-toggle']").Filter(new LocatorFilterOptions { HasText = lens });

    /// <summary>The answer block of <paramref name="visit"/>: "How implement answered" and the answer, or none recorded.</summary>
    public static ILocator Answer(ILocator visit) => visit.Locator("[data-testid='review-answer']");

    /// <summary>Opens the Review tab.</summary>
    public async Task OpenReviewTabAsync() => await Tab("Review").ClickAsync().ConfigureAwait(false);

    public async Task NavigateAsync(Guid id) => await NavigateToAsync($"/runs/{id}").ConfigureAwait(false);

    /// <summary>The checkbox that drops deferred finding <paramref name="findingId"/>.</summary>
    public ILocator DropFinding(string findingId) => ById($"gate-drop-{findingId}");

    /// <summary>The tab header that reads <paramref name="name"/>.</summary>
    public ILocator Tab(string name) => TabHeaders.Filter(new LocatorFilterOptions { HasText = name });

    private ILocator ById(string testId) => _page.Locator($"[data-testid='{testId}']");
}
