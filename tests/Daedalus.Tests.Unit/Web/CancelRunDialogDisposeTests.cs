using System.Reflection;
using Daedalus.Web.Components.Runs;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The dialog's disposal marks it closed, which is what keeps a cancel that finishes after the dialog went away from
///     closing another dialog or reporting into nothing.
/// </summary>
public sealed class CancelRunDialogDisposeTests
{
    private static readonly FieldInfo Closed = typeof(CancelRunDialog).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Red: make <c>Dispose</c> do nothing; the dialog then stays open.</summary>
    [Fact]
    public void Disposing_marks_the_dialog_closed()
    {
        var dialog = new CancelRunDialog();
        ((bool)Closed.GetValue(dialog)!).Should().BeFalse();

        dialog.Dispose();

        ((bool)Closed.GetValue(dialog)!).Should().BeTrue();
    }

    /// <summary>Red: throw from the second disposal; blazor may dispose a component more than once.</summary>
    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var dialog = new CancelRunDialog();
        dialog.Dispose();

        var again = dialog.Dispose;

        again.Should().NotThrow();
    }
}
