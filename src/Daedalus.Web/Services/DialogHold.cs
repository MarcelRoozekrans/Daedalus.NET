using Microsoft.JSInterop;

namespace Daedalus.Web.Services;

/// <summary>
///     Holds a task dialog open while it submits, through <c>js/task-dialog.js</c>: Escape and the title bar's close
///     button do nothing until <see cref="ReleaseAsync"/>. Disposing releases it.
/// </summary>
public sealed class DialogHold(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    /// <summary>Stops the dialog from closing.</summary>
    public async Task HoldAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/task-dialog.js");
        await _module.InvokeVoidAsync("hold");
    }

    /// <summary>Lets the dialog close again. Does nothing when it was never held.</summary>
    public async Task ReleaseAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("release");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("release");
            await _module.DisposeAsync();
            _module = null;
        }
    }
}
