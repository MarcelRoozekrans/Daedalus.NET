namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class TasksPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle => _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Tasks" });
    public ILocator CreateTaskButton => _page.Locator("[data-testid='btn-create-task']");
    public ILocator DataGrid => _page.Locator("[data-testid='tasks-grid']");
    public ILocator DataGridRows => _page.Locator("[data-testid='tasks-grid'] .rz-data-grid-data tbody tr");
    public ILocator LoadingSpinner => _page.Locator("[data-testid='tasks-grid'] .rz-data-grid-loading");
    public ILocator ErrorAlert => _page.Locator(".rz-alert");
    public ILocator Dialog => _page.Locator(".rz-dialog-content");
    public ILocator DialogTitle => _page.Locator(".rz-dialog-title");
    public ILocator ConfirmDialog => _page.Locator(".rz-dialog-wrapper");

    public ILocator ConfirmOkButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Abandon" })
        .Or(_page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete" }));

    public ILocator ConfirmCancelButton =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel" });

    public ILocator Notification => _page.Locator(".rz-notification");

    public ILocator SuccessNotification =>
        _page.Locator(".rz-notification .rz-notification-summary:has-text('Success')");

    public async Task NavigateAsync() => await NavigateToAsync("/tasks").ConfigureAwait(false);

    public ILocator GetRowByText(string text) =>
        _page.Locator("[data-testid='tasks-grid'] .rz-data-grid-data tbody tr", new PageLocatorOptions { HasText = text });

    public ILocator GetEditButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Edit']");

    public ILocator GetDeleteButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Delete']");

    public ILocator GetResumeButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Resume']");

    public ILocator GetAbandonButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Abandon']");

    public ILocator GetStatusBadge(string rowText) =>
        GetRowByText(rowText).Locator(".rz-badge").First;

    public ILocator GetPriorityBadge(string rowText) =>
        GetRowByText(rowText).Locator(".rz-badge.rz-variant-outlined").First;

    public ILocator GetIterationText(string rowText) =>
        GetRowByText(rowText).Locator("td").Nth(4);
}
