namespace Daedalus.Tests.Playwright.Browser.PageObjects;

public sealed class RalphConfigPage(IPage page, Uri baseUrl) : BasePage(page, baseUrl)
{
    public ILocator PageTitle =>
        _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Ralph Config", Exact = true });

    public ILocator LoadingIndicator => _page.Locator(".rz-progressbar-circular");

    public ILocator LoopEngineCard => _page.GetByText("Loop Engine Settings")
        .Locator("xpath=ancestor::div[contains(@class,'rz-card')]");

    public ILocator IterationDelayInput => GetNumericInputByLabel("Iteration Delay (ms)");
    public ILocator MaxFailuresInput => GetNumericInputByLabel("Max Consecutive Failures");
    public ILocator MaxIterationsInput => GetNumericInputByLabel("Max Iterations");
    public ILocator RequestTimeoutInput => GetNumericInputByLabel("Request Timeout (seconds)");
    public ILocator DetailedLoggingCheckbox => GetCheckboxByName("detailedLogging");
    public ILocator MaxSubagentsInput => GetNumericInputByLabel("Max Parallel Subagents");
    public ILocator GitWorkflowCheckbox => GetCheckboxByName("gitWorkflow");
    public ILocator TestInstructionsCheckbox => GetCheckboxByName("testInstructions");
    public ILocator QualityGuardsCheckbox => GetCheckboxByName("qualityGuards");
    public ILocator SelfImprovementCheckbox => GetCheckboxByName("selfImprovement");
    public ILocator CodingStandardsCheckbox => GetCheckboxByName("codingStandards");
    public ILocator LoggingInstructionsCheckbox => GetCheckboxByName("loggingInstructions");
    public ILocator ResetButton => _page.Locator("[data-testid='btn-reset']");
    public ILocator SaveButton => _page.Locator("[data-testid='btn-save']");
    public ILocator ErrorAlert => _page.Locator(".rz-alert");
    public ILocator Notification => _page.Locator(".rz-notification");
    public async Task NavigateAsync() => await NavigateToAsync("/ralph-config").ConfigureAwait(false);

    private ILocator GetNumericInputByLabel(string label) =>
        _page.Locator($".rz-form-field:has(.rz-form-field-label:has-text('{label}')) input");

    /// <summary>
    ///     Radzen CheckBox renders the native input as visually hidden, with a visible
    ///     .rz-chkbox wrapper. Target the wrapper so Playwright's visibility checks pass.
    /// </summary>
    private ILocator GetCheckboxByName(string name) =>
        _page.Locator($".rz-chkbox:has(input[name='{name}'])");
}
