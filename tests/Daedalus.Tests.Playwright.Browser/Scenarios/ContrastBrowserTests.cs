using System.Globalization;
using System.Text.Json.Nodes;
using Daedalus.Tests.Playwright.Browser.Utilities;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     The contract asks for 4.5:1 on text. The 2.13 review measured the page captions, the Manufacture label, the
///     Priority badges and the sidebar's divider captions below it; each is measured here as the browser paints it. The
///     primary colour under white text is a token decision of its own, #374, and is not measured here.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Accessibility")]
[Description("Text contrast: captions, Manufacture, Priority badges and the sidebar's dividers reach 4.5:1")]
public class ContrastBrowserTests : BrowserTestBase
{
    /// <summary>
    ///     Every page's caption, the Body2 line under its H4, and the footer's captions reach 4.5:1 on the body.
    ///     Red: <c>opacity: 0.6</c> back on Tasks' caption in place of the shared caption class; /tasks reads 3.29.
    ///     Red: the shared caption class's colour set to <c>--rz-base-500</c>; every caption and the footer read 2.54.
    ///     Red: <c>opacity: 0.4</c> back on the footer's version in place of the class; it reads 2.09 on every page.
    /// </summary>
    [Test]
    [Description("Every page caption and the footer reach 4.5:1")]
    public async Task Every_page_caption_and_the_footer_reach_text_contrast()
    {
        await TestRoles.WriteAsync(Context, BaseUrl, "admin").ConfigureAwait(false);
        await Page.SetViewportSizeAsync(1920, 1080).ConfigureAwait(false);
        string[] paths = ["/", "/tasks", "/runs", "/projects", "/schedules", "/costs", "/agent", "/prd-generator", "/repositories"];

        var low = new List<string>();
        foreach (var path in paths)
        {
            await TestContext.Progress.WriteLineAsync($"Measuring {path}").ConfigureAwait(false);
            await NavigateToAsync(path).ConfigureAwait(false);
            // The caption is the first Body2 line after the page's H4 title, whatever its tag, under the title or its row.
            var caption = Page.Locator(".rz-body .rz-text-h4").First.Locator("xpath=following::p[contains(@class, 'rz-text-body2')][1]");
            await Expect(caption).ToBeVisibleAsync().ConfigureAwait(false);
            await CheckAsync(low, $"{path} caption", caption).ConfigureAwait(false);

            var footer = Page.Locator(".rz-footer .rz-text-caption");
            foreach (var part in await footer.AllAsync().ConfigureAwait(false))
            {
                await CheckAsync(low, $"{path} footer", part).ConfigureAwait(false);
            }
        }

        string.Join("; ", low).Should().BeEmpty($"every caption reaches {TextContrast.Text}:1");
    }

    /// <summary>
    ///     The signed-out landing's caption, the README's sentence under the product's name, reaches 4.5:1.
    ///     Red: <c>opacity: 0.6</c> back on the landing's caption in place of the shared caption class; it reads 3.29.
    /// </summary>
    [Test]
    [Description("The signed-out landing's caption reaches 4.5:1")]
    public async Task The_landing_caption_reaches_text_contrast()
    {
        await TestAuth.UseAsync(Page, TestAuth.SignedOut).ConfigureAwait(false);
        await NavigateToAsync("/").ConfigureAwait(false);
        var caption = Page.Locator("[data-testid='signed-out-landing'] p.rz-text-body2");
        await Expect(caption).ToBeVisibleAsync().ConfigureAwait(false);

        (await TextContrast.OfAsync(caption).ConfigureAwait(false)).Should().BeGreaterThanOrEqualTo(TextContrast.Text,
            "the landing's caption is text that must be read");
    }

    /// <summary>
    ///     On Tasks, the Manufacture button's label and every Priority badge, Low to Critical, reach 4.5:1. The task list
    ///     is the API's, with its first four tasks given the four priorities, so each badge style shows.
    ///     Red: <c>Shade.Darker</c> dropped from Manufacture; white on <c>--rz-success</c> reads 2.54.
    ///     Red: <c>Shade.Darker</c> dropped from the Priority badge; Low reads 1.23, Medium 3.52, High 2.15, Critical 3.61.
    /// </summary>
    [Test]
    [Description("The Manufacture label and the Priority badges reach 4.5:1")]
    public async Task Manufacture_and_priority_badges_reach_text_contrast()
    {
        await Page.RouteAsync("**/api/tasks?*", async route =>
        {
            var response = await route.FetchAsync().ConfigureAwait(false);
            var body = JsonNode.Parse(await response.TextAsync().ConfigureAwait(false))!;
            var items = body["items"]!.AsArray();
            for (var i = 0; i < Math.Min(4, items.Count); i++)
            {
                items[i]!["priority"] = i;
            }

            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = body.ToJsonString() })
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Page.SetViewportSizeAsync(1920, 1080).ConfigureAwait(false);
        await NavigateToAsync("/tasks").ConfigureAwait(false);

        var grid = Page.Locator("[data-testid='tasks-grid']");
        var manufacture = grid.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Manufacture", Exact = true })
            .First.Locator(".rz-button-text");
        await Expect(manufacture).ToBeVisibleAsync().ConfigureAwait(false);
        var badges = grid.Locator(".rz-badge.rz-variant-outlined");
        await Expect(badges.Filter(new LocatorFilterOptions { HasText = "Critical" }).First).ToBeVisibleAsync()
            .ConfigureAwait(false);

        var low = new List<string>();
        await CheckAsync(low, "Manufacture", manufacture).ConfigureAwait(false);
        foreach (var badge in await badges.AllAsync().ConfigureAwait(false))
        {
            await CheckAsync(low, $"Priority {await badge.InnerTextAsync().ConfigureAwait(false)}", badge).ConfigureAwait(false);
        }

        string.Join("; ", low).Should().BeEmpty($"the Manufacture label and every Priority badge reach {TextContrast.Text}:1");
    }

    /// <summary>
    ///     The sidebar's divider captions, Operations and Admin, reach 4.5:1 on the navy sidebar, at both ends of its
    ///     gradient.
    ///     Red: the divider colour back to <c>rgba(148, 163, 184, 0.5)</c>; Operations and Admin read 2.55.
    /// </summary>
    [Test]
    [Description("The sidebar's divider captions reach 4.5:1")]
    public async Task Sidebar_divider_captions_reach_text_contrast()
    {
        await TestRoles.WriteAsync(Context, BaseUrl, "admin").ConfigureAwait(false);
        await Page.SetViewportSizeAsync(1920, 1080).ConfigureAwait(false);
        await NavigateToAsync("/").ConfigureAwait(false);
        var dividers = Page.Locator(".rz-sidebar .sidebar-section-divider").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex(@"\S"),
        });
        await Expect(dividers.Filter(new LocatorFilterOptions { HasText = "Admin" })).ToBeVisibleAsync().ConfigureAwait(false);

        var low = new List<string>();
        foreach (var divider in await dividers.AllAsync().ConfigureAwait(false))
        {
            await CheckAsync(low, $"divider {await divider.InnerTextAsync().ConfigureAwait(false)}", divider).ConfigureAwait(false);
        }

        string.Join("; ", low).Should().BeEmpty($"every divider caption reaches {TextContrast.Text}:1");
    }

    private static async Task CheckAsync(List<string> low, string what, ILocator element)
    {
        var ratio = await TextContrast.OfAsync(element).ConfigureAwait(false);
        if (ratio < TextContrast.Text)
        {
            low.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {ratio}:1"));
        }
    }
}
