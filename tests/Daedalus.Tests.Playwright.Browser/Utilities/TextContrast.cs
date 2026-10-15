namespace Daedalus.Tests.Playwright.Browser.Utilities;

/// <summary>
///     Measures the WCAG contrast ratio of an element's text against what is painted behind it, as the browser computes
///     both: the text colour with its alpha and the opacity of the element and its ancestors, over the nearest
///     background. A background with alpha is laid over the ones under it; a gradient counts at its worst stop.
/// </summary>
public static class TextContrast
{
    /// <summary>The ratio WCAG 2.2 AA asks of normal text.</summary>
    public const double Text = 4.5;

    /// <summary>The contrast ratio of <paramref name="element"/>'s text, rounded to two decimals.</summary>
    public static Task<double> OfAsync(ILocator element) => element.EvaluateAsync<double>(Script);

    private const string Script = """
        async el => {
            // A finite animation, such as the page's fade-in, is let finish first: mid-fade the text is fainter.
            await Promise.all(document.getAnimations()
                .filter(a => a.effect && a.effect.getComputedTiming().iterations !== Infinity)
                .map(a => a.finished.catch(() => null)));
            const parse = c => {
                const m = /rgba?\(([^)]+)\)/.exec(c || '');
                if (!m) { return null; }
                const p = m[1].split(/[\s,\/]+/).filter(Boolean).map(Number);
                return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
            };
            const over = (top, under) => ({
                r: top.r * top.a + under.r * (1 - top.a),
                g: top.g * top.a + under.g * (1 - top.a),
                b: top.b * top.a + under.b * (1 - top.a),
                a: 1,
            });
            const lum = c => {
                const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
                return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
            };

            let opacity = 1;
            for (let e = el; e; e = e.parentElement) { opacity *= parseFloat(getComputedStyle(e).opacity); }

            // The layers behind the text, nearest first, down to the first opaque colour or gradient.
            const layers = [];
            let base = [{ r: 255, g: 255, b: 255, a: 1 }];
            for (let e = el; e; e = e.parentElement) {
                const s = getComputedStyle(e);
                if (s.backgroundImage && s.backgroundImage !== 'none') {
                    const stops = Array.from(s.backgroundImage.matchAll(/rgba?\([^)]+\)/g)).map(m => parse(m[0]));
                    if (stops.length) { base = stops.map(stop => over(stop, { r: 255, g: 255, b: 255, a: 1 })); break; }
                }
                const c = parse(s.backgroundColor);
                if (c && c.a > 0) {
                    if (c.a >= 1) { base = [c]; break; }
                    layers.push(c);
                }
            }

            const fg = parse(getComputedStyle(el).color);
            fg.a *= opacity;
            const ratios = base.map(b => {
                const bg = layers.reduceRight((under, layer) => over(layer, under), b);
                const l1 = lum(over(fg, bg));
                const l2 = lum(bg);
                return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
            });
            return Math.round(Math.min(...ratios) * 100) / 100;
        }
        """;
}
