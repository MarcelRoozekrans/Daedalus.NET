using System.Text.RegularExpressions;

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

    public ILocator ConfirmOkButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete", Exact = true });

    public ILocator ConfirmCancelButton =>
        _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel" });

    public ILocator Notification => _page.Locator(".rz-notification");

    public ILocator SuccessNotification =>
        _page.Locator(".rz-notification .rz-notification-summary:has-text('Success')");

    public async Task NavigateAsync() => await NavigateToAsync("/tasks").ConfigureAwait(false);

    public ILocator GetRowByText(string text) =>
        _page.Locator("[data-testid='tasks-grid'] .rz-data-grid-data tbody tr", new PageLocatorOptions { HasText = text });

    public ILocator GetEditButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Edit task']");

    public ILocator GetDeleteButton(string rowText) =>
        GetRowByText(rowText).Locator("button[title='Delete task']");

    public ILocator GetManufactureButton(string rowText) =>
        GetRowByText(rowText).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Manufacture", Exact = true });

    public ILocator GetStatusBadge(string rowText) =>
        GetRowByText(rowText).Locator(".rz-badge").First;

    public ILocator GetPriorityBadge(string rowText) =>
        GetRowByText(rowText).Locator(".rz-badge.rz-variant-outlined").First;

    /// <summary>The grid's header cell that reads exactly <paramref name="title"/>.</summary>
    public ILocator Header(string title) =>
        _page.Locator("[data-testid='tasks-grid'] thead th").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new Regex($@"^\s*{Regex.Escape(title)}\s*$"),
        });

    /// <summary>The visible text label inside a row's Manufacture button.</summary>
    public ILocator GetManufactureLabel(string rowText) =>
        GetManufactureButton(rowText).Locator(".rz-button-text");

    /// <summary>The row's pull request cell content: a link, or a dash.</summary>
    public ILocator GetPullRequestCell(string rowText) =>
        GetRowByText(rowText).Locator("[data-testid='task-pr']");

    /// <summary>The page's alert for a task that was created but whose manufacture was refused.</summary>
    public ILocator ManufactureRefusedAlert => _page.Locator("[data-testid='tasks-manufacture-refused']");

    // ── The create and edit dialogs ──────────────────────────────────────────

    /// <summary>The dialog's outer box, as Radzen positions it.</summary>
    public ILocator DialogBox => _page.Locator(".rz-dialog");

    public ILocator ProjectPicker => Dialog.Locator("[data-testid='task-project']");
    public ILocator TitleInput => Dialog.Locator("[data-testid='task-title']");
    public ILocator DescriptionInput => Dialog.Locator("[data-testid='task-description']");
    public ILocator DialogAlert => Dialog.Locator("[data-testid='task-dialog-error']");

    public ILocator CreateButton =>
        Dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create", Exact = true });

    public ILocator CreateAndManufactureButton =>
        Dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create and manufacture", Exact = true });

    public ILocator DialogCancelButton =>
        Dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true });

    /// <summary>The open picker's option for the project <paramref name="name"/>.</summary>
    public ILocator ProjectOption(string name) =>
        _page.Locator(".rz-dropdown-item").Filter(new LocatorFilterOptions { HasText = name });

    /// <summary>Opens the create dialog and waits until it shows.</summary>
    public async Task OpenCreateDialogAsync()
    {
        await CreateTaskButton.ClickAsync().ConfigureAwait(false);
        await Assertions.Expect(ProjectPicker).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>Picks <paramref name="project"/>, then fills the title and description.</summary>
    public async Task FillNewTaskAsync(string project, string title, string description)
    {
        await ProjectPicker.ClickAsync().ConfigureAwait(false);
        await ProjectOption(project).First.ClickAsync().ConfigureAwait(false);
        await TitleInput.FillAsync(title).ConfigureAwait(false);
        await DescriptionInput.FillAsync(description).ConfigureAwait(false);
    }
}
