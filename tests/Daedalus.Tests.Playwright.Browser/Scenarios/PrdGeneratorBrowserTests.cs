using Daedalus.Application.DTOs;
using Daedalus.Tests.Playwright.Browser.PageObjects;
using Complexity = Daedalus.Domain.Entities.Complexity;
using Priority = Daedalus.Domain.Entities.Priority;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("PrdGenerator")]
[Description("PRD Generator browser tests validating multi-step wizard rendering and navigation")]
public class PrdGeneratorBrowserTests : BrowserTestBase
{
    private PrdGeneratorPage _prdPage = null!;

    public override async Task SetUpAsync()
    {
        await base.SetUpAsync().ConfigureAwait(false);
        if (!SetUpCompleted)
            return;
        _prdPage = new PrdGeneratorPage(Page, BaseUrl);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    [Test]
    [Description("PRD Generator page should render title and subtitle")]
    public async Task PrdGenerator_ShouldRender_TitleAndSubtitle()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.PageTitle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Subtitle).ToBeVisibleAsync().ConfigureAwait(false);
    }

    [Test]
    [Description("PRD Generator should display the steps indicator")]
    public async Task PrdGenerator_ShouldRender_StepsIndicator()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps).ToBeVisibleAsync().ConfigureAwait(false);

        var stepCount = await _prdPage.StepItems.CountAsync().ConfigureAwait(false);
        stepCount.Should().Be(4, "PRD wizard has 4 steps");
    }

    [Test]
    [Description("Steps indicator should show all four step labels")]
    public async Task PrdGenerator_Steps_ShouldDisplayAllLabels()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(_prdPage.Steps.GetByText("Select Project")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps.GetByText("Requirements")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps.GetByText("Review PRD")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(_prdPage.Steps.GetByText("Tasks Created")).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Step 1: Project Selection ────────────────────────────────────────────

    [Test]
    [Description("Step 1 should display the project selection heading")]
    public async Task PrdGenerator_Step1_ShouldDisplayProjectSelection()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Expect(_prdPage.SelectProjectHeading).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    [Test]
    [Description("Step 1 should show project cards for seeded projects")]
    public async Task PrdGenerator_Step1_ShouldShowProjectCards()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        // The E2E fixture seeds a "Test Project"
        await Expect(_prdPage.GetProjectCardByName("Test Project")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    [Test]
    [Description("Clicking a project card should advance to step 2 (Requirements)")]
    public async Task PrdGenerator_Step1_ClickProject_ShouldAdvanceToStep2()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await Expect(_prdPage.GetProjectCardByName("Test Project")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);

        await _prdPage.GetProjectCardByName("Test Project").ClickAsync().ConfigureAwait(false);

        // Step 2 should now be visible: both the Generate PRD and Cancel buttons render (Or() would be a strict-mode violation)
        await Expect(_prdPage.CancelButton).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Expect(_prdPage.GenerateButton).ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Step 2: Requirements Input ───────────────────────────────────────────

    [Test]
    [Description("Step 2 should have a Cancel button to return to project selection")]
    public async Task PrdGenerator_Step2_CancelButton_ShouldResetToStep1()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        // Advance to step 2
        await Expect(_prdPage.GetProjectCardByName("Test Project")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await _prdPage.GetProjectCardByName("Test Project").ClickAsync().ConfigureAwait(false);

        await Expect(_prdPage.CancelButton).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
        await _prdPage.CancelButton.ClickAsync().ConfigureAwait(false);

        // Should return to step 1
        await Expect(_prdPage.SelectProjectHeading).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 }).ConfigureAwait(false);
    }

    // ── No error on load ─────────────────────────────────────────────────────

    [Test]
    [Description("PRD Generator should not show error alerts on initial load")]
    public async Task PrdGenerator_ShouldNotDisplay_ErrorAlert()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);

        await Expect(_prdPage.ErrorAlert).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    // ── Steps 3 and 4: generate, review, convert ─────────────────────────────
    // Phase 2.14, R7 and R8: generate and convert call the model, so the two PRD endpoints are answered here with the
    // API's own DTOs serialized as the API serializes them, in camelCase.

    private const string Requirements = "Let a shop owner count stock from a phone.";
    private const string Overview = "A stock counter for small shops.";
    private const string Goal = "Count a shelf in under a minute.";
    private const string FeatureTitle = "Scan a barcode";
    private const string TaskTitle = "Store counts offline";
    private const string CreatedPrompt = "Implement barcode scanning for the stock counter.";
    private const string GenerateRefusal = "The model could not be reached.";

    private static readonly Guid CreatedTaskId = new(0x7a5c0000, 0x0000, 0x4000, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01);

    private static string WebJson(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static PrdResponseDto GeneratedPrd(Guid projectId) => new(
        "prd-1", projectId, Overview, [Goal], ["Shops have a phone with a camera."],
        [new PrdItemDto(FeatureTitle, "Read a barcode with the camera.", "A scanned code fills the item.", Priority.High, Complexity.Medium)],
        [new PrdItemDto(TaskTitle, "Keep counts when offline.", "Counts survive a lost connection.", Priority.Medium, Complexity.Low)],
        new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc));

    private static TaskDto CreatedTask(Guid projectId) => new(
        CreatedTaskId, "TASK-PRD-1", projectId, FeatureTitle, "Read a barcode with the camera.", 2, "Implementation", 1, [], [], 2,
        CreatedPrompt, "DONE", 10, 0, null, null, 0, new DateTime(2026, 10, 16, 9, 5, 0, DateTimeKind.Utc), null, null, null, [], null,
        null, null, null);

    /// <summary>Answers generate with <see cref="GeneratedPrd"/> and convert with one created task; returns each request's body.</summary>
    private async Task<(List<string> Generate, List<string> Convert)> RoutePrdAsync()
    {
        var generate = new List<string>();
        var convert = new List<string>();
        await Page.RouteAsync("**/api/prd/generate", async route =>
        {
            generate.Add(route.Request.PostData ?? string.Empty);
            var projectId = JsonDocument.Parse(route.Request.PostData!).RootElement.GetProperty("projectId").GetGuid();
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = WebJson(GeneratedPrd(projectId)),
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Page.RouteAsync("**/api/prd/convert-to-tasks", async route =>
        {
            convert.Add(route.Request.PostData ?? string.Empty);
            var projectId = JsonDocument.Parse(route.Request.PostData!).RootElement.GetProperty("projectId").GetGuid();
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = WebJson(new[] { CreatedTask(projectId) }),
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return (generate, convert);
    }

    private async Task GenerateForTestProjectAsync()
    {
        await _prdPage.NavigateAsync().ConfigureAwait(false);
        await _prdPage.GetProjectCardByName("Test Project").ClickAsync().ConfigureAwait(false);
        await _prdPage.RequirementsTextArea.FillAsync(Requirements).ConfigureAwait(false);
        await _prdPage.RequirementsTextArea.PressAsync("Tab").ConfigureAwait(false);
        await _prdPage.GenerateButton.ClickAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Generate posts the project and requirements, and the review step shows the PRD's overview, its goals and every
    ///     item. The answer is camelCase, as the API writes it: this is R8's red test.
    ///     Red: <c>PrdGenerator</c> deserializes without <c>ApiJson</c>, case-sensitively; it binds nulls and the review
    ///     step throws on <c>Prd.Goals</c>, so the review heading never shows. The convert scenario fails with it too.
    ///     Red: the request carries <c>Guid.Empty</c> for the project; the posted project id is not Test Project's.
    ///     Red: the success branch leaves <c>_currentStep</c> at RequirementsInput; the review heading never shows.
    ///     Red: the review renders <c>Prd.Id</c> for the overview; the overview text does not show.
    ///     Red: the Goals list renders the first assumption for each goal; the goal does not show.
    ///     Red: <c>AllItems</c> returns only the features; the heading reads (1), and the task item is missing.
    /// </summary>
    [Test]
    [Description("Generate shows the PRD's overview, goals and items for review")]
    public async Task PrdGenerator_Generate_ShowsTheReviewStep()
    {
        var (generate, _) = await RoutePrdAsync().ConfigureAwait(false);
        await GenerateForTestProjectAsync().ConfigureAwait(false);

        await Expect(Page.GetByText("Review Generated PRD")).ToBeVisibleAsync().ConfigureAwait(false);
        var posted = JsonDocument.Parse(generate.Single()).RootElement;
        posted.GetProperty("projectId").GetGuid().Should().NotBe(Guid.Empty);
        posted.GetProperty("userRequirements").GetString().Should().Be(Requirements);
        await Expect(Page.GetByText(Overview)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByText(Goal)).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByText("Items to Convert to Tasks (2)")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByText(TaskTitle, new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Converting the selected items posts only those, and the last step lists the created tasks.
    ///     Red: <c>HandleConvert</c> converts every item, not the selected ones; the posted items include the task item.
    ///     Red: the success branch leaves <c>_currentStep</c> at PrdReview; "Tasks Created Successfully!" never shows.
    ///     Red: the success alert counts <c>0</c>; it does not read "1 tasks have been created".
    ///     Red: the Prompt column binds <c>Title</c>; the created task's prompt does not show.
    /// </summary>
    [Test]
    [Description("Convert posts the selected items and lists the created tasks")]
    public async Task PrdGenerator_Convert_ListsTheCreatedTasks()
    {
        var (_, convert) = await RoutePrdAsync().ConfigureAwait(false);
        await GenerateForTestProjectAsync().ConfigureAwait(false);
        await Page.GetByText(FeatureTitle, new PageGetByTextOptions { Exact = true }).ClickAsync().ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Convert 1 Items to Tasks" }).ClickAsync()
            .ConfigureAwait(false);

        await Expect(Page.GetByText("Tasks Created Successfully!")).ToBeVisibleAsync().ConfigureAwait(false);
        var items = JsonDocument.Parse(convert.Single()).RootElement.GetProperty("prdItems").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()).ToList();
        items.Should().Equal(FeatureTitle);
        await Expect(Page.GetByText("1 tasks have been created and are ready to execute.")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.Locator("tr.rz-data-row", new PageLocatorOptions { HasText = CreatedPrompt })).ToBeVisibleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     A generate the API refuses shows the refusal's reason and stays on the requirements step.
    ///     Red: the failure branch sets a fixed "Failed to generate PRD"; the reason does not show.
    ///     Red: the failure branch moves to PrdReview; the requirements step's Generate button is gone.
    /// </summary>
    [Test]
    [Description("A refused generate says why and stays on the requirements")]
    public async Task PrdGenerator_RefusedGenerate_SaysWhy()
    {
        await Page.RouteAsync("**/api/prd/generate", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 400,
            ContentType = "application/json",
            Body = WebJson(new { error = GenerateRefusal }),
        })).ConfigureAwait(false);
        await GenerateForTestProjectAsync().ConfigureAwait(false);

        await Expect(_prdPage.ErrorAlert.Filter(new LocatorFilterOptions { HasText = GenerateRefusal })).ToBeVisibleAsync()
            .ConfigureAwait(false);
        await Expect(_prdPage.GenerateButton).ToBeVisibleAsync().ConfigureAwait(false);
    }
}
