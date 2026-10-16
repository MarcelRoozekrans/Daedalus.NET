namespace Daedalus.Tests.Playwright.Browser.Utilities;

/// <summary>
///     Measures where a page's elements sit against the layout's footer and its scrolling body, as the browser lays them
///     out once the page's fade-in has finished.
/// </summary>
public static class LayoutFit
{
    /// <summary>
    ///     How far <paramref name="element"/>'s bottom edge sits below the footer's top edge, in CSS pixels. Zero or less
    ///     is above the footer.
    /// </summary>
    public static async Task<double> OverlapWithFooterAsync(IPage page, ILocator element)
    {
        await SettleAsync(page).ConfigureAwait(false);
        var box = (await element.BoundingBoxAsync().ConfigureAwait(false))!;
        var footer = (await page.Locator(".rz-footer").BoundingBoxAsync().ConfigureAwait(false))!;
        return box.Y + box.Height - footer.Y;
    }

    /// <summary>How far the layout's body can scroll, in CSS pixels: its scroll height past its client height.</summary>
    public static async Task<double> BodyOverflowAsync(IPage page)
    {
        await SettleAsync(page).ConfigureAwait(false);
        return await page.Locator(".rz-body").EvaluateAsync<double>("b => b.scrollHeight - b.clientHeight").ConfigureAwait(false);
    }

    /// <summary>
    ///     How far <paramref name="page"/>'s bottom edge sits above the content column's bottom, in CSS pixels: the body's
    ///     inner bottom less its own and the page wrapper's bottom padding. Zero is a page that fills the column.
    /// </summary>
    public static async Task<double> GapBelowAsync(IPage page, ILocator root)
    {
        await SettleAsync(page).ConfigureAwait(false);
        return await root.EvaluateAsync<double>("""
            el => {
                const body = el.closest('.rz-body');
                const wrapper = el.parentElement;
                const inner = body.getBoundingClientRect().top + body.clientTop + body.clientHeight
                    - parseFloat(getComputedStyle(body).paddingBottom)
                    - parseFloat(getComputedStyle(wrapper).paddingBottom);
                return Math.round(inner - el.getBoundingClientRect().bottom);
            }
            """).ConfigureAwait(false);
    }

    /// <summary>Lets every finite animation finish: mid-fade the page sits 4px lower.</summary>
    private static async Task SettleAsync(IPage page) => await page.EvaluateAsync("""
        () => Promise.all(document.getAnimations()
            .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
            .map(a => a.finished.catch(() => undefined)))
        """).ConfigureAwait(false);
}
