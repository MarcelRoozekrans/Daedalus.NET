namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class PrdGeneratorPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle =>
        _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "PRD Generator" });

    public ILocator Subtitle => _page.GetByText("AI-powered requirements to executable tasks");

    // Steps indicator
    public ILocator Steps => _page.Locator(".rz-steps");
    public ILocator StepItems => _page.Locator(".rz-steps-item");

    // Step 1: Project Selection
    public ILocator SelectProjectHeading => _page.GetByText("Select Project");
    public ILocator ProjectCards => _page.Locator(".rz-card[style*='cursor: pointer']");
    public ILocator ProjectLoadingSpinner => _page.Locator(".rz-progressbar-circular");
    public ILocator NoProjectsAlert => _page.GetByText("No projects found");

    public ILocator GetProjectCardByName(string name) =>
        _page.Locator(".rz-card", new PageLocatorOptions { HasText = name });

    // Step 2: Requirements Input
    public ILocator RequirementsHeading => _page.GetByText("Enter Requirements");
    public ILocator RequirementsTextArea => _page.Locator("textarea").First;
    public ILocator ContextTextArea => _page.Locator("textarea").Last;
    public ILocator GenerateButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Generate PRD" });
    public ILocator CancelButton => _page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel" });

    // Error alert
    public ILocator ErrorAlert => _page.Locator(".rz-alert");

    public async Task NavigateAsync() => await NavigateToAsync("/prd-generator").ConfigureAwait(false);
}
