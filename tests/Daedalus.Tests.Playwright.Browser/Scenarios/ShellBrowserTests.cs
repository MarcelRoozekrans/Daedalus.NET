using Daedalus.Tests.Playwright.Browser.Fixtures;
using Daedalus.Tests.Playwright.Browser.Utilities;

namespace Daedalus.Tests.Playwright.Browser.Scenarios;

/// <summary>
///     The console's shell as the phase 2.13 contract draws it: the menu grouped around the path, Executions gone, and on a
///     phone, at 375×812, an overlay menu, a compact header and no page that scrolls sideways. Desktop is 1920×1080.
/// </summary>
[TestFixture]
[Category("E2E")]
[Category("Browser")]
[Category("Navigation")]
[Description("The shell: grouped menu, phone overlay menu, compact header, removed and tidied pages")]
public class ShellBrowserTests : BrowserTestBase
{
    private const int PhoneWidth = 375;
    private const int PhoneHeight = 812;
    private const int DesktopWidth = 1920;
    private const int DesktopHeight = 1080;

    private ILocator Toggle => Page.Locator(".rz-sidebar-toggle");
    private ILocator Sidebar => Page.Locator(".rz-sidebar");
    private ILocator Backdrop => Page.Locator("[data-testid='menu-backdrop']");
    private ILocator UserMenuButton => Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "User menu", Exact = true });

    /// <summary>
    ///     The menu, top to bottom, is exactly the contract's: the path, then the two front doors, then Operations, then,
    ///     for an admin only, Admin with Git Repositories. A divider shows as <c>--</c> and its caption. Executions is in
    ///     neither list.
    ///     Red: Executions left in the menu; both lists hold it.
    ///     Red: Agent left under Operations; Agent comes after Costs.
    ///     Red: the Admin divider outside the <c>AuthorizeView</c>; the developer's list ends with <c>--Admin</c>.
    ///     Red: the first item still labelled Dashboard; neither list starts with Home.
    /// </summary>
    [TestCase("developer", false)]
    [TestCase("admin", true)]
    [Description("The menu lists exactly the contracted items, in order")]
    public async Task The_menu_lists_exactly_the_contracted_items_in_order(string role, bool admin)
    {
        await TestRoles.WriteAsync(Context, BaseUrl, role).ConfigureAwait(false);
        await Page.SetViewportSizeAsync(DesktopWidth, DesktopHeight).ConfigureAwait(false);
        await OpenAsync("/").ConfigureAwait(false);

        List<string> expected =
        [
            "Home", "Tasks", "Runs", "Projects",
            "--", "PRD Generator", "Agent",
            "--Operations", "Schedules", "Costs",
        ];
        if (admin)
        {
            expected.AddRange(["--Admin", "Git Repositories"]);
        }

        var menu = await MenuWhenSettledAsync(expected).ConfigureAwait(false);

        menu.Should().Equal(expected, $"the menu for a {role} is the contract's, in order: {string.Join(" | ", menu)}");
    }

    /// <summary>
    ///     <c>/executions</c> is no page any more: the router's not-found content shows, inside the signed-in chrome.
    ///     Red: <c>Pages/Executions.razor</c> restored; the Task Loop History page renders and no not-found alert shows.
    /// </summary>
    [Test]
    [Description("Executions is gone: /executions gives the not-found page")]
    public async Task Executions_is_gone()
    {
        await OpenAsync("/executions").ConfigureAwait(false);

        await Expect(Page.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Sorry, there's nothing at this address." }))
            .ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     On a phone the opened menu lies over the page, behind it a backdrop, and the page keeps its place and width; a
    ///     menu click navigates and closes it.
    ///     Red: a closed sidebar not hidden; Radzen's closed sidebar keeps a 1px edge, visible.
    ///     Red: no <c>position: fixed</c> for the phone sidebar, Radzen's push layout; the body moves right by 259px.
    ///     Red: the body narrowed to 200px while the menu is open; its width differs by 175px.
    ///     Red: the phone sidebar at <c>left: 100px</c>; it no longer starts at the page's left edge.
    ///     Red: no backdrop rendered; the backdrop is never visible.
    ///     Red: <c>LocationChanged</c> does not close the menu; after the click to Tasks the backdrop is still there.
    ///     The closed sidebar after the navigation goes red under the same mutation, after the backdrop.
    /// </summary>
    [Test]
    [Description("On a phone the menu overlays the page and closes on navigation")]
    public async Task On_a_phone_the_menu_overlays_and_closes_on_navigation()
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight).ConfigureAwait(false);
        await OpenAsync("/").ConfigureAwait(false);
        await Expect(Sidebar).ToBeHiddenAsync().ConfigureAwait(false);
        var closed = await BodyBoxAsync().ConfigureAwait(false);

        await Toggle.ClickAsync().ConfigureAwait(false);
        await Expect(Sidebar).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Backdrop).ToBeVisibleAsync().ConfigureAwait(false);
        await WaitForSidebarWidthAsync(260).ConfigureAwait(false);

        var open = await BodyBoxAsync().ConfigureAwait(false);
        open.X.Should().BeApproximately(closed.X, 1, "the page does not move when the menu opens");
        open.Width.Should().BeApproximately(closed.Width, 1, "the page keeps its width when the menu opens");
        var sidebar = await Sidebar.BoundingBoxAsync().ConfigureAwait(false);
        sidebar!.X.Should().BeApproximately(0, 1, "the menu lies over the page's left edge");

        await Sidebar.GetByText("Tasks", new LocatorGetByTextOptions { Exact = true }).ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/tasks").ConfigureAwait(false);

        await Expect(Backdrop).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(Sidebar).ToBeHiddenAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The opened phone menu takes the focus and keeps it while open; Escape and a tap on the backdrop each close it,
    ///     and the focus goes back to the toggle.
    ///     Red: no Escape handler; after Escape the backdrop is still visible.
    ///     Red: the focus left where it was on close; the active element is not the toggle.
    ///     Red: no focus trap; a Tab from the last menu item leaves the menu.
    ///     Red: Shift+Tab left to the browser; from the first item it goes back to the toggle.
    ///     Red: <c>trap</c> does not focus the first item; the opened menu does not take the focus.
    ///     Red: the backdrop closes the menu without restoring the focus; after the tap it is not on the toggle.
    ///     Red: the backdrop has no click handler; after the tap it is still visible.
    /// </summary>
    [Test]
    [Description("The phone menu closes on Escape and on the backdrop, returning the focus to the toggle")]
    public async Task The_phone_menu_closes_on_escape_and_backdrop_and_returns_focus_to_the_toggle()
    {
        await TestRoles.WriteAsync(Context, BaseUrl, "admin").ConfigureAwait(false);
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight).ConfigureAwait(false);
        await OpenAsync("/").ConfigureAwait(false);

        await Toggle.ClickAsync().ConfigureAwait(false);
        await Expect(Backdrop).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Sidebar.GetByText("Git Repositories", new LocatorGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync().ConfigureAwait(false);
        await WaitForFocusAsync(InSidebar, "the opened menu takes the focus").ConfigureAwait(false);

        // The focus is on the first item: Shift+Tab wraps to the last one, not back to the toggle before the menu.
        await Page.Keyboard.PressAsync("Shift+Tab").ConfigureAwait(false);
        (await Page.EvaluateAsync<bool>(InSidebar).ConfigureAwait(false))
            .Should().BeTrue($"Shift+Tab from the first item stays in the menu, not on {await FocusedAsync().ConfigureAwait(false)}");

        // More presses than the menu has items, so the focus would wrap out of an untrapped menu.
        for (var i = 0; i < 15; i++)
        {
            await Page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            (await Page.EvaluateAsync<bool>(InSidebar).ConfigureAwait(false))
                .Should().BeTrue($"the focus stays in the open menu after Tab {i + 1}, not on {await FocusedAsync().ConfigureAwait(false)}");
        }

        await Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await Expect(Backdrop).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(Sidebar).ToBeHiddenAsync().ConfigureAwait(false);
        await WaitForFocusAsync(OnToggle, "Escape returns the focus to the toggle").ConfigureAwait(false);

        await Toggle.ClickAsync().ConfigureAwait(false);
        await Expect(Backdrop).ToBeVisibleAsync().ConfigureAwait(false);
        await WaitForFocusAsync(InSidebar, "the reopened menu takes the focus").ConfigureAwait(false);
        await Backdrop.ClickAsync(new LocatorClickOptions { Position = new Position { X = 340, Y = 400 } })
            .ConfigureAwait(false);
        await Expect(Backdrop).ToHaveCountAsync(0).ConfigureAwait(false);
        await Expect(Sidebar).ToBeHiddenAsync().ConfigureAwait(false);
        await WaitForFocusAsync(OnToggle, "a backdrop tap returns the focus to the toggle").ConfigureAwait(false);
    }

    /// <summary>
    ///     On a desktop the menu is no overlay: it stays open on navigation and has no backdrop.
    ///     Red: the menu closes on every navigation, not only on a phone; after the click to Tasks the sidebar is hidden.
    ///     Red: the backdrop rendered whenever the menu is open, with no phone rule and click-through; it is visible on
    ///     the desktop. With its clicks caught, the click to Tasks times out under it instead.
    /// </summary>
    [Test]
    [Description("On a desktop the menu stays open on navigation and has no backdrop")]
    public async Task On_a_desktop_the_menu_stays_open_on_navigation_with_no_backdrop()
    {
        await Page.SetViewportSizeAsync(DesktopWidth, DesktopHeight).ConfigureAwait(false);
        await OpenAsync("/").ConfigureAwait(false);
        await Expect(Sidebar).ToBeVisibleAsync().ConfigureAwait(false);

        await Sidebar.GetByText("Tasks", new LocatorGetByTextOptions { Exact = true }).ClickAsync().ConfigureAwait(false);
        await Page.WaitForURLAsync("**/tasks").ConfigureAwait(false);
        await Expect(Page.Locator(".rz-body h4").First).ToBeVisibleAsync().ConfigureAwait(false);

        await Expect(Sidebar).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Backdrop).Not.ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     The menu toggle, the user menu and the menu itself are named for a screen reader, and the user menu holds the
    ///     user's name and Log out.
    ///     Red: <c>ToggleAriaLabel</c> left at Radzen's default; no button is named Menu.
    ///     Red: the user menu button without its <c>aria-label</c>; no button is named User menu.
    ///     Red: the sidebar without <c>role="navigation"</c> and its label; no navigation is named Main.
    ///     Red: the user menu panel without <c>role="menu"</c>; no menu is named User menu.
    ///     Red: the user's name left out of the menu; it does not contain E2E Test User.
    ///     Red: Log out without <c>role="menuitem"</c>; the menu has no Log out item.
    /// </summary>
    [Test]
    [Description("The menu toggle and the user menu have accessible names")]
    public async Task The_menu_toggle_and_user_menu_have_accessible_names()
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight).ConfigureAwait(false);
        await OpenAsync("/").ConfigureAwait(false);

        var toggle = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Menu", Exact = true });
        await Expect(toggle).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(UserMenuButton).ToBeVisibleAsync().ConfigureAwait(false);

        await toggle.ClickAsync().ConfigureAwait(false);
        await Expect(Page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Main", Exact = true }))
            .ToBeVisibleAsync().ConfigureAwait(false);
        await Page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await Expect(Backdrop).ToHaveCountAsync(0).ConfigureAwait(false);

        await UserMenuButton.ClickAsync().ConfigureAwait(false);
        var menu = Page.GetByRole(AriaRole.Menu, new PageGetByRoleOptions { Name = "User menu", Exact = true });
        await Expect(menu).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(menu).ToContainTextAsync("E2E Test User").ConfigureAwait(false);
        await Expect(menu.GetByRole(AriaRole.Menuitem, new LocatorGetByRoleOptions { Name = "Log out", Exact = true }))
            .ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     At 375px no page scrolls sideways: neither the document nor the layout's body, which is the scroll container
    ///     Radzen gives the page, the document itself never scrolling sideways under it. Grids scroll inside their card.
    ///     Red: PrdGenerator's steps back to Radzen's unwrapped row; /prd-generator's body scrolls by 194px.
    ///     Red: the theme's alert wrap rule removed; /schedules' body scrolls by 146px past an alert's buttons.
    ///     Red: <c>.rz-body &gt; div { min-width: 2000px; }</c> in the theme; the body scrolls sideways on every page.
    ///     Red: <c>html { min-width: 2000px; }</c> in the theme; the document scrolls sideways.
    /// </summary>
    [Test]
    [Description("No page overflows at 375px")]
    public async Task No_page_overflows_at_375px()
    {
        await TestRoles.WriteAsync(Context, BaseUrl, "admin").ConfigureAwait(false);
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight).ConfigureAwait(false);
        string[] paths =
        [
            "/", "/tasks", "/runs", $"/runs/{E2EServerFixture.Runs.AwaitingApproval}", "/projects", "/schedules", "/costs",
            "/agent", "/prd-generator", "/repositories",
        ];

        var overflows = new List<string>();
        foreach (var path in paths)
        {
            await TestContext.Progress.WriteLineAsync($"Measuring {path}").ConfigureAwait(false);
            await OpenAsync(path).ConfigureAwait(false);
            await Expect(Page.Locator(".rz-body :is(h1, h2, h3, h4, h5, h6)").First).ToBeVisibleAsync().ConfigureAwait(false);
            var json = await Page.EvaluateAsync<string>(OverflowScript).ConfigureAwait(false);
            var overflow = JsonSerializer.Deserialize<Overflow>(json)!;
            if (overflow.Document > 0 || overflow.Body > 0)
            {
                overflows.Add($"{path}: document {overflow.Document}px, body {overflow.Body}px ({overflow.Widest})");
            }
        }

        overflows.Should().BeEmpty("no page scrolls sideways at 375px");
    }

    /// <summary>
    ///     A generated PRD whose text holds a word longer than a phone is wide wraps inside the column: the review step
    ///     does not push the page sideways. The PRD is the API's answer, fulfilled here, so no model runs.
    ///     Red: <c>overflow-wrap: anywhere</c> dropped from <c>.prd-generator</c>; the body scrolls by 1892px.
    ///     Red: <c>html { min-width: 2000px; }</c> in the theme; the document scrolls sideways.
    /// </summary>
    [Test]
    [Description("A long PRD wraps inside the column on a phone")]
    public async Task A_long_prd_wraps_inside_the_column_on_a_phone()
    {
        var word = new string('x', 300);
        var prd = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = "prd-e2e",
            ["ProjectId"] = Guid.Empty,
            ["ProductOverview"] = $"An overview with {word} in it.",
            ["Goals"] = new[] { $"A goal naming {word}." },
            ["Assumptions"] = Array.Empty<string>(),
            ["Features"] = Array.Empty<object>(),
            ["Tasks"] = Array.Empty<object>(),
            ["GeneratedAt"] = DateTime.UtcNow,
        };
        await Page.RouteAsync("**/api/prd/generate", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(prd),
        })).ConfigureAwait(false);
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight).ConfigureAwait(false);
        await OpenAsync("/prd-generator").ConfigureAwait(false);

        await Page.Locator(".rz-card[style*='cursor: pointer']").Filter(new LocatorFilterOptions { HasText = "Test Project" })
            .ClickAsync().ConfigureAwait(false);
        await Page.Locator("textarea").First.FillAsync("Requirements for a phone.").ConfigureAwait(false);
        // The text area binds on change, so the button enables once the focus leaves it.
        await Page.Locator("textarea").First.PressAsync("Tab").ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Generate PRD" }).ClickAsync().ConfigureAwait(false);
        await Expect(Page.GetByText("Review Generated PRD")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByText("An overview with")).ToBeVisibleAsync().ConfigureAwait(false);

        var overflow = JsonSerializer.Deserialize<Overflow>(
            await Page.EvaluateAsync<string>(OverflowScript).ConfigureAwait(false))!;
        overflow.Body.Should().BeLessThanOrEqualTo(0, $"the review step stays in the column ({overflow.Widest})");
        overflow.Document.Should().BeLessThanOrEqualTo(0, "the document does not scroll sideways");
    }

    /// <summary>
    ///     On a phone the header is one line: the toggle, the logo and the user menu fit across it, and nothing in it
    ///     wraps or runs past its edge. The user's name and Log out, in the user menu, are one line each.
    ///     Red: the phone rule hiding the full user removed, the name and Log out back in the header; the name wraps.
    ///     Red: the user menu button padded by 1rem; the header is 70px tall.
    ///     Red: the user menu given a 400px minimum width; the header's content runs 224px past its edge.
    ///     Red: <c>white-space: nowrap</c> dropped from the user menu; the name takes three lines, Log out two.
    /// </summary>
    [Test]
    [Description("The header does not wrap on a phone")]
    public async Task The_header_does_not_wrap_on_a_phone()
    {
        await Page.SetViewportSizeAsync(PhoneWidth, PhoneHeight).ConfigureAwait(false);
        await OpenAsync("/").ConfigureAwait(false);
        await Expect(UserMenuButton).ToBeVisibleAsync().ConfigureAwait(false);

        var header = JsonSerializer.Deserialize<HeaderBox>(
            await Page.EvaluateAsync<string>(HeaderScript).ConfigureAwait(false))!;
        header.Wrapped.Should().BeEmpty("no text in the header takes more than one line");
        header.Height.Should().BeLessThanOrEqualTo(64, "the header is one line");
        header.Overflow.Should().BeLessThanOrEqualTo(0, "nothing runs past the header's edge");

        await UserMenuButton.ClickAsync().ConfigureAwait(false);
        var menu = Page.GetByRole(AriaRole.Menu, new PageGetByRoleOptions { Name = "User menu", Exact = true });
        await Expect(menu).ToBeVisibleAsync().ConfigureAwait(false);
        var lines = await menu.EvaluateAsync<int[]>(MenuLinesScript).ConfigureAwait(false);
        lines.Should().Equal([1, 1], "the name and Log out each take one line");
    }

    /// <summary>
    ///     The Agent page opens with the shared page header: the H4 Agent and a Body2 caption under it.
    ///     Red: the header removed from <c>Agent.razor</c>; the page has no H4.
    ///     Red: the caption made a Caption, not Body2; the H4 has no Body2 sibling.
    ///     Red: the caption's text removed; the caption is empty.
    /// </summary>
    [Test]
    [Description("The Agent page has a heading")]
    public async Task The_agent_page_has_a_heading()
    {
        await OpenAsync("/agent").ConfigureAwait(false);

        var heading = Page.Locator(".rz-body h4");
        await Expect(heading).ToHaveTextAsync("Agent").ConfigureAwait(false);
        var caption = Page.Locator(".rz-body h4 + p.rz-text-body2");
        await Expect(caption).ToHaveCountAsync(1).ConfigureAwait(false);
        (await caption.InnerTextAsync().ConfigureAwait(false)).Trim().Should().NotBeEmpty("the heading has a caption");
    }

    /// <summary>
    ///     While the console downloads, the loading screen is the body background, the product name and a progress
    ///     indicator in the primary colour, not the full-viewport gradient. The framework script is held back, so the
    ///     screen stays.
    ///     Red: the gradient restored on <c>.app-loading</c>; its background image is not none.
    ///     Red: <c>--rz-base-200</c> as its background; it is not the body background.
    ///     Red: the product name removed; the screen does not contain Daedalus.
    ///     Red: the progress indicator without <c>role="progressbar"</c>; no progress bar is found.
    ///     Red: the indicator drawn in <c>--rz-on-primary</c>; its colour is not the primary colour.
    /// </summary>
    [Test]
    [Description("The loading screen is the body background, the product name and a primary progress indicator")]
    public async Task The_loading_screen_follows_the_design_system()
    {
        await Page.RouteAsync("**/_framework/blazor.webassembly*.js", route => route.AbortAsync()).ConfigureAwait(false);
        await Page.GotoAsync(new Uri(BaseUrl, "/").ToString()).ConfigureAwait(false);

        var loading = Page.Locator(".app-loading");
        await Expect(loading).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(loading).ToContainTextAsync("Daedalus").ConfigureAwait(false);
        var progress = loading.GetByRole(AriaRole.Progressbar);
        await Expect(progress).ToBeVisibleAsync().ConfigureAwait(false);

        var colours = JsonSerializer.Deserialize<LoadingColours>(
            await Page.EvaluateAsync<string>(LoadingScript).ConfigureAwait(false))!;
        colours.Image.Should().Be("none", "the loading screen has no gradient");
        colours.Background.Should().Be(colours.Body, "the loading screen is the body background");
        colours.Indicator.Should().Be(colours.Primary, "the progress indicator is drawn in the primary colour");
    }

    private async Task OpenAsync(string path)
    {
        await Page.GotoAsync(new Uri(BaseUrl, path).ToString()).ConfigureAwait(false);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
        await Expect(Toggle).ToBeVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>The menu once it holds what <paramref name="expected"/> does, or as it stands after ten seconds.</summary>
    private async Task<List<string>> MenuWhenSettledAsync(List<string> expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var menu = (await Page.EvaluateAsync<string[]>(MenuScript).ConfigureAwait(false)).ToList();
            if (menu.SequenceEqual(expected, StringComparer.Ordinal) || DateTime.UtcNow > deadline)
            {
                return menu;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private async Task<BodyBox> BodyBoxAsync()
    {
        var box = await Page.Locator(".rz-body").BoundingBoxAsync().ConfigureAwait(false);
        return new BodyBox(box!.X, box.Width);
    }

    private async Task WaitForSidebarWidthAsync(double width)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await Sidebar.BoundingBoxAsync().ConfigureAwait(false))?.Width < width - 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private async Task WaitForFocusAsync(string script, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var focused = await Page.EvaluateAsync<bool>(script).ConfigureAwait(false);
        while (!focused && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
            focused = await Page.EvaluateAsync<bool>(script).ConfigureAwait(false);
        }

        focused.Should().BeTrue(because);
    }

    private Task<string> FocusedAsync() =>
        Page.EvaluateAsync<string>("() => document.activeElement ? document.activeElement.outerHTML.slice(0, 160) : 'nothing'");

    private const string InSidebar = "() => !!document.activeElement && !!document.activeElement.closest('.rz-sidebar')";

    private const string OnToggle = "() => !!document.activeElement && !!document.activeElement.closest('.rz-sidebar-toggle')";

    private const string MenuScript = """
        () => Array.from(document.querySelectorAll('.rz-sidebar :is(.sidebar-section-divider, .rz-navigation-item-link)'))
            .map(e => {
                if (e.classList.contains('sidebar-section-divider')) { return '--' + e.textContent.trim(); }
                const copy = e.cloneNode(true);
                copy.querySelectorAll('.rzi, .rz-badge').forEach(x => x.remove());
                return copy.textContent.trim();
            })
        """;

    private const string OverflowScript = """
        () => {
            const d = document.documentElement;
            const body = document.querySelector('.rz-body');
            const widest = Array.from(body.querySelectorAll('*'))
                .map(e => [e, e.getBoundingClientRect().right])
                .sort((a, b) => b[1] - a[1])[0];
            return JSON.stringify({
                Document: d.scrollWidth - d.clientWidth,
                Body: body.scrollWidth - body.clientWidth,
                Widest: widest ? widest[0].tagName + '.' + String(widest[0].className).slice(0, 40) : '',
            });
        }
        """;

    private const string HeaderScript = """
        () => {
            const header = document.querySelector('.rz-header');
            const walker = document.createTreeWalker(header, NodeFilter.SHOW_TEXT);
            const wrapped = [];
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                if (!node.textContent.trim()) { continue; }
                const range = document.createRange();
                range.selectNodeContents(node);
                const tops = new Set(Array.from(range.getClientRects()).filter(r => r.width > 0).map(r => Math.round(r.top)));
                if (tops.size > 1) { wrapped.push(node.textContent.trim()); }
            }
            return JSON.stringify({
                Wrapped: wrapped,
                Height: header.getBoundingClientRect().height,
                Overflow: header.scrollWidth - header.clientWidth,
            });
        }
        """;

    /// <summary>The number of lines the user's name and Log out each take, from their text's line boxes.</summary>
    private const string MenuLinesScript = """
        menu => ['[data-testid="user-menu-name"]', '[role="menuitem"]'].map(selector => {
            const walker = document.createTreeWalker(menu.querySelector(selector), NodeFilter.SHOW_TEXT);
            const tops = new Set();
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                if (!node.textContent.trim()) { continue; }
                const range = document.createRange();
                range.selectNodeContents(node);
                Array.from(range.getClientRects()).filter(r => r.width > 0).forEach(r => tops.add(Math.round(r.top)));
            }
            return tops.size;
        })
        """;

    private const string LoadingScript = """
        () => {
            const loading = document.querySelector('.app-loading');
            const indicator = loading.querySelector('[role="progressbar"]');
            const probe = document.createElement('div');
            probe.style.color = 'var(--rz-primary)';
            probe.style.backgroundColor = 'var(--rz-body-background-color)';
            document.body.appendChild(probe);
            const result = {
                Image: getComputedStyle(loading).backgroundImage,
                Background: getComputedStyle(loading).backgroundColor,
                Body: getComputedStyle(probe).backgroundColor,
                Primary: getComputedStyle(probe).color,
                Indicator: getComputedStyle(indicator).borderTopColor,
            };
            probe.remove();
            return JSON.stringify(result);
        }
        """;

    private sealed record BodyBox(double X, double Width);

    private sealed record Overflow(double Document, double Body, string Widest);

    private sealed record HeaderBox(string[] Wrapped, double Height, double Overflow);

    private sealed record LoadingColours(string Image, string Background, string Body, string Primary, string Indicator);
}
