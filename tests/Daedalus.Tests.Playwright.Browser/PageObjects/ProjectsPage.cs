namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class ProjectsPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle => _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Projects" });
    public ILocator CreateProjectButton => _page.Locator("[data-testid='btn-create-project']");
    public ILocator DataGrid => _page.Locator("[data-testid='projects-grid']");
    public ILocator DataGridRows => _page.Locator("[data-testid='projects-grid'] .rz-data-grid-data tbody tr");
    public ILocator ErrorAlert => _page.Locator(".rz-alert");
    public ILocator InfoAlert => _page.Locator(".rz-alert:has-text('No projects found')");
    public ILocator Dialog => _page.Locator(".rz-dialog-content");
    public ILocator ConfirmOkButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete" });

    public ILocator ConfirmCancelButton =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel" });

    public ILocator Notification => _page.Locator(".rz-notification");
    public async Task NavigateAsync() => await NavigateToAsync("/projects").ConfigureAwait(false);

    public ILocator GetRowByText(string text) =>
        _page.Locator("[data-testid='projects-grid'] .rz-data-grid-data tbody tr", new PageLocatorOptions { HasText = text });

    public ILocator GetEditButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Edit']");

    public ILocator GetDeleteButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Delete']");
}
