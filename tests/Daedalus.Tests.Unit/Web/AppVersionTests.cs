using Daedalus.Web.Services;

namespace Daedalus.Tests.Unit.Web;

/// <summary>Phase 2.13: the footer shows the console's own version, not a hardcoded one.</summary>
public sealed class AppVersionTests
{
    /// <summary>
    ///     The SDK appends the source revision after a <c>+</c>; the footer shows the version without it, and keeps a
    ///     prerelease label, which comes before the <c>+</c>.
    ///     Red: Display returns its input; "0.1.0+8c549ada" keeps its suffix.
    ///     Red: Display cuts at the first '-'; "1.2.0-beta.3" loses its label.
    /// </summary>
    [Theory]
    [InlineData("0.1.0+8c549ada1f", "0.1.0")]
    [InlineData("1.2.0-beta.3+8c549ada1f", "1.2.0-beta.3")]
    [InlineData("0.1.0", "0.1.0")]
    public void The_version_is_shown_without_its_build_metadata(string informational, string shown) =>
        AppVersion.Display(informational).Should().Be(shown);

    /// <summary>
    ///     An assembly with no informational version shows none, rather than a "v" with nothing after it.
    ///     Red: Display returns its input, never cutting at '+'; "+8c549ada1f" is shown.
    ///     Red: the check for nothing left before the '+' dropped; "+8c549ada1f" shows "".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+8c549ada1f")]
    public void No_version_shows_none(string? informational) => AppVersion.Display(informational).Should().BeNull();
}
