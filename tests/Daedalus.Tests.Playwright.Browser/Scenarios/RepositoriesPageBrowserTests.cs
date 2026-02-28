using Daedalus.Tests.Playwright.Browser.PageObjects;

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
        if (!SetUpCompleted) return;
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
