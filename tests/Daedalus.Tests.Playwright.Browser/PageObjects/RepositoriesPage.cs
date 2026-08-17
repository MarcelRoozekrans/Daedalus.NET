namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class RepositoriesPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle =>
        _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Git Repositories" });

    // Header button; the empty state renders a second "Add Repository" button (see EmptyStateAddButton).
    public ILocator AddRepositoryButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add Repository" }).First;

    // Loading state
    public ILocator LoadingSpinner => _page.Locator(".rz-progressbar-circular");

    // Empty state
    public ILocator EmptyState => _page.Locator(".empty-state");
    public ILocator EmptyStateTitle => _page.GetByText("No Repositories Configured");
    public ILocator EmptyStateAddButton => _page.Locator(".empty-state .rz-button");

    // Repository cards
    public ILocator RepositoryCards => _page.Locator(".rz-card");
    public ILocator GetCardByName(string name) =>
        _page.Locator(".rz-card", new PageLocatorOptions { HasText = name });

    public ILocator GetActiveBadge(string cardName) =>
        GetCardByName(cardName).Locator(".rz-badge");

    public ILocator GetEditButton(string cardName) =>
        GetCardByName(cardName).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit" });

    public ILocator GetTestButton(string cardName) =>
        GetCardByName(cardName).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Test" });

    public ILocator GetDeleteButton(string cardName) =>
        GetCardByName(cardName).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete" });

    // Dialog elements
    public ILocator Dialog => _page.Locator(".rz-dialog-content");
    public ILocator DialogTitle => _page.Locator(".rz-dialog-title");
    public ILocator DialogNameInput => _page.Locator(".rz-dialog-content input").First;
    public ILocator DialogUrlInput => _page.Locator(".rz-dialog-content input").Nth(1);
    public ILocator DialogCancelButton =>
        _page.Locator(".rz-dialog-content").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel" });
    public ILocator DialogSubmitButton =>
        _page.Locator(".rz-dialog-content").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create" })
            .Or(_page.Locator(".rz-dialog-content").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Update" }));

    // Confirm dialog
    public ILocator ConfirmDeleteButton =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete" }).Last;
    public ILocator ConfirmCancelButton =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel" });

    // Notification
    public ILocator Notification => _page.Locator(".rz-notification");

    public async Task NavigateAsync() => await NavigateToAsync("/repositories").ConfigureAwait(false);
}
