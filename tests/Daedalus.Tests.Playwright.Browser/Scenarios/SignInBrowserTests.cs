using System.Collections.Concurrent;
using Daedalus.Tests.Playwright.Browser.Utilities;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     Signed-out visitors and expired sessions go to sign-in, never to page errors (#350). Each test boots the console
///     in a test sign-in mode of its own (<see cref="TestAuth"/>); there is no identity provider in this suite, so the
///     assertions stop at the console's own sign-in route, <c>authentication/login</c>, and the return URL it carries.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("SignIn")]
[Description("Signed-out and expired sessions lead to sign-in")]
public class SignInBrowserTests : BrowserTestBase
{
    private const string LandingSentence =
        "High-performance .NET 10 application using Railway-Oriented Programming for AI-driven task execution.";

    /// <summary>
    ///     Every page that loads data as it opens, each of which must send an expired session to sign in. Executions
    ///     loads nothing until a search, and the run pages need a user who may view runs, which opens the run hub.
    /// </summary>
    private static readonly string[] DataPages =
    [
        "/tasks", "/projects", "/costs", "/schedules", "/repositories", "/prd-generator", "/agent",
    ];

    private readonly ConcurrentQueue<string> _apiRequests = new();

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
        {
            return;
        }

        // NUnit shares one fixture instance across its tests: each starts with no requests seen.
        _apiRequests.Clear();
        Page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                _apiRequests.Enqueue(request.Url);
            }
        };
    }

    private ILocator Header => Page.Locator(".rz-header");

    private ILocator HeaderSignIn =>
        Header.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Sign in", Exact = true });

    /// <summary>
    ///     A signed-out visit to a data page goes to the console's sign-in, carrying the page to come back to, and asks
    ///     the API for nothing on the way.
    ///     Red: drop <c>[Authorize]</c> from Tasks.razor; the page renders and the URL stays /tasks.
    ///     Red: make RedirectToLogin call NavigateTo, not NavigateToLogin; the return URL is null.
    ///     Red: have MainLayout load the task board as it initializes; /api/tasks is requested.
    /// </summary>
    [Test]
    public async Task A_signed_out_visit_to_tasks_redirects_to_sign_in()
    {
        await TestAuth.UseAsync(Page, TestAuth.SignedOut).ConfigureAwait(false);

        await NavigateToAsync("/tasks").ConfigureAwait(false);

        await Page.WaitForURLAsync("**/authentication/login").ConfigureAwait(false);
        (await TestAuth.ReturnUrlAsync(Page).ConfigureAwait(false)).Should()
            .Be(new Uri(BaseUrl, "/tasks").AbsoluteUri, "sign-in returns to the page the visitor asked for");
        _apiRequests.Should().BeEmpty("a signed-out visitor's page is never built, so it asks for no data");
    }

    /// <summary>
    ///     A signed-out visitor at / sees the landing: the product name, one sentence and Sign in, with no menu, no
    ///     toggle and no data. Sign in starts the console's sign-in and returns to /.
    ///     Red: render HomeDashboard instead of SignedOutLanding in Home.razor; the landing is missing.
    ///     Red: title the landing "Welcome"; the Daedalus heading is missing.
    ///     Red: change the landing sentence; the text assertion fails.
    ///     Red: label LoginDisplay's button "Log in"; the header has no Sign in.
    ///     Red: render RadzenSidebar whoever is signed in; the sidebar count is 1.
    ///     Red: render RadzenSidebarToggle whoever is signed in; the toggle count is 1.
    ///     Red: render HomeDashboard beside SignedOutLanding; /api/tasks is requested.
    ///     Red: make the landing's button call NavigateTo, not NavigateToLogin; the return URL is null.
    /// </summary>
    [Test]
    public async Task The_signed_out_home_shows_the_landing_and_no_menu()
    {
        await TestAuth.UseAsync(Page, TestAuth.SignedOut).ConfigureAwait(false);

        await NavigateToAsync("/").ConfigureAwait(false);

        var landing = Page.Locator("[data-testid='signed-out-landing']");
        await Expect(landing).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(landing.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Daedalus", Exact = true }))
            .ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(landing).ToContainTextAsync(LandingSentence).ConfigureAwait(false);
        await Expect(HeaderSignIn).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.Locator(".rz-sidebar")).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(Page.Locator(".rz-sidebar-toggle")).ToHaveCountAsync(0).ConfigureAwait(false);
        _apiRequests.Should().BeEmpty("the landing shows no data");

        await landing.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Sign in", Exact = true })
            .ClickAsync().ConfigureAwait(false);

        await Page.WaitForURLAsync("**/authentication/login").ConfigureAwait(false);
        (await TestAuth.ReturnUrlAsync(Page).ConfigureAwait(false)).Should()
            .Be(new Uri(BaseUrl, "/").AbsoluteUri, "the landing's sign-in returns to /");
    }

    /// <summary>
    ///     A token refresh that fails mid-form sends the user to sign in, returning to the same page; the stored OIDC user
    ///     is cleared and the header stops naming the user, so it never claims a dead session.
    ///     Red: rename the grid's data-testid in Tasks.razor; the grid precondition fails.
    ///     Red: show "Signed in" in place of the user's name in LoginDisplay; the header precondition fails.
    ///     Red: drop the SessionExpiryHandler from the token-mode API client in Program.cs; the create fails in place
    ///     and the URL stays /tasks.
    ///     Red: redirect with NavigateTo in SessionExpiry, not the exception's Redirect; the return URL is null.
    ///     Red: skip ClearStoredUserAsync in SessionExpiry; the seeded user is still in sessionStorage.
    ///     Red: drop the Expired check from MainLayout's Apply; the header still names E2E Test User.
    ///     Red: label LoginDisplay's button "Log in"; the header has no Sign in.
    /// </summary>
    [Test]
    public async Task An_expired_session_redirects_to_sign_in_and_back()
    {
        await TestAuth.UseAsync(Page, TestAuth.Token).ConfigureAwait(false);
        await TestAuth.SeedStoredUserAsync(Page).ConfigureAwait(false);
        await NavigateToAsync("/tasks").ConfigureAwait(false);
        await Expect(Page.Locator("[data-testid='tasks-grid']")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Header).ToContainTextAsync("E2E Test User").ConfigureAwait(false);

        await Page.Locator("[data-testid='btn-create-task']").ClickAsync().ConfigureAwait(false);
        var dialog = Page.Locator(".rz-dialog-content");
        await dialog.Locator("input").First.FillAsync("Expired mid-form").ConfigureAwait(false);
        await dialog.Locator("textarea").Nth(1).FillAsync("Write the thing.").ConfigureAwait(false);
        await TestAuth.ExpireTokenAsync(Page).ConfigureAwait(false);
        // The button's accessible name carries its icon's ligature, so the name is matched, not equalled.
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create Task" })
            .ClickAsync().ConfigureAwait(false);

        await Page.WaitForURLAsync("**/authentication/login").ConfigureAwait(false);
        (await TestAuth.ReturnUrlAsync(Page).ConfigureAwait(false)).Should()
            .Be(new Uri(BaseUrl, "/tasks").AbsoluteUri, "sign-in returns to the page the form was on");
        (await TestAuth.HasStoredUserAsync(Page).ConfigureAwait(false)).Should()
            .BeFalse("a session that cannot refresh is cleared, so no later read claims it");
        await Expect(Header).Not.ToContainTextAsync("E2E Test User").ConfigureAwait(false);
        await Expect(HeaderSignIn).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     With a session that cannot get a token, every data page goes to sign-in and returns to itself, and none ever
    ///     shows "Please log in", even for a moment. The user may not view runs, so the run hub never opens and each
    ///     redirect is the page's own request.
    ///     Red: restore the code before #350: the "Please log in to access this data." failure in ApiClient.GetAsync
    ///     and no SessionExpiryHandler on the token-mode client; /tasks shows it. The message alone does not turn it
    ///     red: the redirect replaces the page before the failure renders.
    ///     Red: drop the SessionExpiryHandler from the token-mode API client in Program.cs; /tasks stays put.
    /// </summary>
    [Test]
    public async Task No_page_shows_please_log_in()
    {
        await TestAuth.UseAsync(Page, TestAuth.Token).ConfigureAwait(false);
        await TestAuth.ExpireTokenFromStartAsync(Page).ConfigureAwait(false);
        await Page.AddInitScriptAsync(
            $"localStorage.setItem('{TestRoles.Key}', 'task-manager,project-manager,analyst');").ConfigureAwait(false);
        await TestAuth.WatchForTextAsync(Page, "Please log in").ConfigureAwait(false);

        var stayed = new List<string>();
        var showed = new List<string>();
        foreach (var path in DataPages)
        {
            await NavigateToAsync(path).ConfigureAwait(false);
            try
            {
                await Page.WaitForURLAsync("**/authentication/login", new PageWaitForURLOptions { Timeout = 10_000 })
                    .ConfigureAwait(false);
                var returnUrl = await TestAuth.ReturnUrlAsync(Page).ConfigureAwait(false);
                if (!string.Equals(returnUrl, new Uri(BaseUrl, path).AbsoluteUri, StringComparison.Ordinal))
                {
                    stayed.Add($"{path} returned to {returnUrl ?? "nothing"}");
                }
            }
            catch (TimeoutException)
            {
                stayed.Add($"{path} stayed at {new Uri(Page.Url).AbsolutePath}");
            }

            // Each visit loads a new document, which starts its own record.
            showed.AddRange(await TestAuth.PathsThatShowedAsync(Page).ConfigureAwait(false));
        }

        showed.Should().BeEmpty("no page tells the user to log in; it sends them to sign in");
        stayed.Should().BeEmpty("every data page sends an expired session to sign in and back to itself");
    }
}
