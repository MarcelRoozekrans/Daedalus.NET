using System.Globalization;
using Daedalus.Tests.Playwright.Browser.PageObjects;
using Daedalus.Tests.Playwright.Browser.Utilities;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Repositories")]
[Description("Git Repositories page browser tests validating card layout, CRUD dialogs, and empty state")]
public class RepositoriesPageBrowserTests : BrowserTestBase
{
    private RepositoriesPage _reposPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _reposPage = new RepositoriesPage(Page, BaseUrl);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    [Test]
    [Description("Repositories page should render title and Add Repository button")]
    public async Task RepositoriesPage_ShouldRender_TitleAndAddButton()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_reposPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.AddRepositoryButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Repositories page should show empty state when no repositories exist")]
    public async Task RepositoriesPage_ShouldDisplay_EmptyState()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        // The E2E fixture does not seed repositories, so empty state should appear
        await Expect(_reposPage.EmptyStateTitle).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    [Test]
    [Description("Empty state should have an Add Repository button")]
    public async Task RepositoriesPage_EmptyState_ShouldHaveAddButton()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await Expect(_reposPage.EmptyStateTitle).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_reposPage.EmptyStateAddButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.EmptyStateAddButton).ToContainTextAsync("Add Repository").ConfigureAwait(false);
    }

    // ── Refused and failed reads ──────────────────────────────────────────────

    /// <summary>
    ///     A user who is not an admin and opens the page by its URL is refused by the API with 403. The page says the
    ///     repositories are an admin's, as a Warning, and shows no Add button and no empty state: a refusal is not an
    ///     empty list. The fixture's API signs every request in as admin, so the 403 is the API's answer, fulfilled here.
    ///     Red: the read's 403 handled as before, as an empty list; no Warning alert shows.
    ///     Red: the header's Add Repository rendered whatever the read said; one Add button shows.
    ///     Red: the empty state rendered for a refused read as well; "No Repositories Configured" shows.
    /// </summary>
    [Test]
    [Description("A refused read says repositories are an admin's, with no Add button or empty state")]
    public async Task A_refused_read_says_repositories_are_an_admins()
    {
        await TestRoles.WriteAsync(Context, BaseUrl, "developer").ConfigureAwait(false);
        await Page.RouteAsync("**/api/repositories", route => route.FulfillAsync(new RouteFulfillOptions { Status = 403 }))
            .ConfigureAwait(false);
        await _reposPage.NavigateAsync().ConfigureAwait(false);

        var refused = Page.Locator(".rz-alert.rz-warning", new PageLocatorOptions { HasText = "Git repositories are managed by admins." });
        await Expect(refused).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add Repository" })).ToHaveCountAsync(0)
            .ConfigureAwait(false);
        await Expect(_reposPage.EmptyStateTitle).ToHaveCountAsync(0).ConfigureAwait(false);
    }

    /// <summary>
    ///     A read that fails for another reason shows the contract's error state: a Danger alert with the status and
    ///     Retry, and no empty state. Retry reads again, and the page then shows what the API holds.
    ///     Red: every failed read swallowed into an empty list, as before; no Danger alert shows.
    ///     Red: the empty state rendered beside the error; "No Repositories Configured" shows.
    ///     Red: Retry without a handler; the alert stays after the read would succeed.
    /// </summary>
    [Test]
    [Description("A failed read shows an error with Retry, not an empty list")]
    public async Task A_failed_read_shows_an_error_with_retry()
    {
        var fail = true;
        await Page.RouteAsync("**/api/repositories", async route =>
        {
            if (fail)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 500 }).ConfigureAwait(false);
            }
            else
            {
                await route.ContinueAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await _reposPage.NavigateAsync().ConfigureAwait(false);

        var error = Page.Locator(".rz-alert.rz-danger", new PageLocatorOptions { HasText = "The repositories could not be loaded" });
        await Expect(error).ToContainTextAsync("500").ConfigureAwait(false);
        await Expect(_reposPage.EmptyStateTitle).ToHaveCountAsync(0).ConfigureAwait(false);

        fail = false;
        await error.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry" }).ClickAsync().ConfigureAwait(false);
        await Expect(error).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(_reposPage.EmptyStateTitle).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Listed repositories, answered by the test ─────────────────────────────

    private const string ListedName = "Listed Repository";

    private static readonly Guid ListedId = new(0x6d3f1c2a, 0x5b7e, 0x4f10, 0x9a, 0x2b, 0x3c, 0x4d, 0x5e, 0x6f, 0x7a, 0x8b);

    /// <summary>Answers the list on <paramref name="page"/> with one repository, added at <paramref name="createdAt"/>.</summary>
    private static Task RouteOneRepositoryAsync(IPage page, DateTime createdAt) =>
        page.RouteAsync("**/api/repositories", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = ListedId, name = ListedName, url = "https://github.com/example/listed.git", platform = "GitHub",
                    defaultBranch = "main", authenticationMethod = (string?)null, credentialIdentifier = (string?)null,
                    isActive = true, createdAt = createdAt.ToString("O", CultureInfo.InvariantCulture),
                    lastUsedAt = (string?)null, description = (string?)null,
                },
            }),
        }));

    /// <summary>Refuses <paramref name="method"/> on the listed repository with <paramref name="status"/>.</summary>
    private Task RefuseWriteAsync(string method, int status) =>
        Page.RouteAsync($"**/api/repositories/{ListedId}", async route =>
        {
            if (string.Equals(route.Request.Method, method, StringComparison.Ordinal))
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = status,
                    ContentType = "application/problem+json",
                    Body = """{"detail":"Refused by the test."}""",
                }).ConfigureAwait(false);
                return;
            }

            await route.ContinueAsync().ConfigureAwait(false);
        });

    /// <summary>
    ///     The card's Added time is the viewer's local time: in Auckland, 23:30 UTC on 15 October is 12:30 PM on the 16th.
    ///     Red: the card shows <c>repo.CreatedAt.ToString("g")</c>, unconverted; it reads the UTC time.
    /// </summary>
    [Test]
    [Description("A repository card shows when it was added in the viewer's time zone")]
    public async Task A_repository_card_shows_when_it_was_added_in_local_time()
    {
        var created = new DateTime(2026, 10, 15, 23, 30, 0, DateTimeKind.Utc);
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var expected = TimeZoneInfo.ConvertTimeFromUtc(created, auckland).ToString("g", CultureInfo.GetCultureInfo("en-US"));
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            TimezoneId = "Pacific/Auckland",
            Locale = "en-US",
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await RouteOneRepositoryAsync(page, created).ConfigureAwait(false);
        var repos = new RepositoriesPage(page, BaseUrl);
        await repos.NavigateAsync().ConfigureAwait(false);

        await Expect(repos.GetCardByName(ListedName)).ToContainTextAsync($"Added {expected}").ConfigureAwait(false);
    }

    /// <summary>
    ///     An update the API refuses or fails says so in an Error notification with what failed and the status, and never
    ///     in a success message.
    ///     Red: the refused branch notifies Success "The repository was updated."; no Error notification shows.
    ///     Red: Refused returns what failed and the HTTP version, not the status; the notification does not name it.
    ///     Red: the success notification sent before the status is checked; a success message showed.
    /// </summary>
    [TestCase(403)]
    [TestCase(500)]
    [Description("A refused update says why, and never that it succeeded")]
    public async Task A_refused_update_says_why_and_never_that_it_succeeded(int status)
    {
        await RouteOneRepositoryAsync(Page, new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        await RefuseWriteAsync("PUT", status).ConfigureAwait(false);
        await TestAuth.WatchForAsync(Page, ".rz-notification-success").ConfigureAwait(false);
        await _reposPage.NavigateAsync().ConfigureAwait(false);

        await _reposPage.GetEditButton(ListedName).ClickAsync().ConfigureAwait(false);
        await _reposPage.DialogSubmitButton.ClickAsync().ConfigureAwait(false);

        var error = Page.Locator(".rz-notification-error");
        await Expect(error).ToContainTextAsync("Failed to update repository.").ConfigureAwait(false);
        await Expect(error).ToContainTextAsync($"The server answered {status}").ConfigureAwait(false);
        (await TestAuth.PathsThatShowedAsync(Page, ".rz-notification-success").ConfigureAwait(false))
            .Should().BeEmpty("a refused update never says it succeeded");
    }

    /// <summary>
    ///     A delete the API refuses or fails says so in an Error notification with what failed and the status, and never
    ///     in a success message.
    ///     Red: the refused branch notifies Success "The repository was removed."; no Error notification shows.
    ///     Red: Refused returns what failed and the HTTP version, not the status; the notification does not name it.
    ///     Red: the success notification sent before the status is checked; a success message showed.
    /// </summary>
    [TestCase(403)]
    [TestCase(500)]
    [Description("A refused delete says why, and never that it succeeded")]
    public async Task A_refused_delete_says_why_and_never_that_it_succeeded(int status)
    {
        await RouteOneRepositoryAsync(Page, new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        await RefuseWriteAsync("DELETE", status).ConfigureAwait(false);
        await TestAuth.WatchForAsync(Page, ".rz-notification-success").ConfigureAwait(false);
        await _reposPage.NavigateAsync().ConfigureAwait(false);

        await _reposPage.GetDeleteButton(ListedName).ClickAsync().ConfigureAwait(false);
        await _reposPage.ConfirmDeleteButton.ClickAsync().ConfigureAwait(false);

        var error = Page.Locator(".rz-notification-error");
        await Expect(error).ToContainTextAsync("Failed to delete repository.").ConfigureAwait(false);
        await Expect(error).ToContainTextAsync($"The server answered {status}").ConfigureAwait(false);
        (await TestAuth.PathsThatShowedAsync(Page, ".rz-notification-success").ConfigureAwait(false))
            .Should().BeEmpty("a refused delete never says it succeeded");
    }

    // ── Add Repository dialog ────────────────────────────────────────────────

    [Test]
    [Description("Clicking Add Repository should open a dialog")]
    public async Task RepositoriesPage_AddButton_ShouldOpenDialog()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await _reposPage.AddRepositoryButton.ClickAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.DialogTitle).ToContainTextAsync("Add Repository").ConfigureAwait(false);
    }

    [Test]
    [Description("Add Repository dialog should contain form fields")]
    public async Task RepositoriesPage_AddDialog_ShouldContainFormFields()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await _reposPage.AddRepositoryButton.ClickAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);

        // Verify form fields are present
        await Expect(_reposPage.Dialog.GetByText("Repository Name")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog.GetByText("Repository URL")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog.GetByText("Platform")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog.GetByText("Default Branch")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Add Repository dialog should have Cancel and Create buttons")]
    public async Task RepositoriesPage_AddDialog_ShouldHaveCancelAndCreateButtons()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await _reposPage.AddRepositoryButton.ClickAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_reposPage.DialogCancelButton).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.DialogSubmitButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking Cancel in the Add dialog should close it")]
    public async Task RepositoriesPage_AddDialogCancel_ShouldCloseDialog()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await _reposPage.AddRepositoryButton.ClickAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);

        await _reposPage.DialogCancelButton.ClickAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Empty state Add button ───────────────────────────────────────────────

    [Test]
    [Description("Clicking Add Repository from empty state should open the dialog")]
    public async Task RepositoriesPage_EmptyStateAddButton_ShouldOpenDialog()
    {
        await _reposPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await Expect(_reposPage.EmptyStateAddButton).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        await _reposPage.EmptyStateAddButton.ClickAsync().ConfigureAwait(false);
        await Expect(_reposPage.Dialog).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_reposPage.DialogTitle).ToContainTextAsync("Add Repository").ConfigureAwait(false);
    }
}
