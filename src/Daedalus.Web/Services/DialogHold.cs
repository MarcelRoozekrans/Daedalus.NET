using Microsoft.JSInterop;

namespace Daedalus.Web.Services;

/// <summary>
///     Holds a task dialog open while it submits, through <c>js/task-dialog.js</c>: Escape and the title bar's close
///     button do nothing until <see cref="ReleaseAsync"/>. The dialog loads the module on its first render, through
///     <see cref="Prepare"/>, so a submit never waits on it. Disposing releases a hold, and once disposed a hold is never
///     taken: a dialog that closed while the module loaded leaves no window listeners behind.
/// </summary>
public sealed class DialogHold(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;
    private bool _held;

    /// <summary>Whether the dialog has gone; a held dialog's submit stops when it has.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Starts loading the module. Called on the dialog's first render; safe to call again.</summary>
    public void Prepare() => _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/task-dialog.js").AsTask();

    /// <summary>
    ///     Stops the dialog from closing. Returns false, and holds nothing, when the dialog has gone, before or while the
    ///     module loads.
    /// </summary>
    public async Task<bool> HoldAsync()
    {
        if (IsDisposed)
        {
            return false;
        }

        Prepare();
        var module = await _module!;
        if (IsDisposed)
        {
            return false;
        }

        // Marked before the call, so a dispose that runs while it is in flight releases it.
        _held = true;
        await module.InvokeVoidAsync("hold");
        return !IsDisposed;
    }

    /// <summary>Lets the dialog close again. Does nothing when it is not held.</summary>
    public async Task ReleaseAsync()
    {
        if (!_held || _module is null)
        {
            return;
        }

        _held = false;
        await (await _module).InvokeVoidAsync("release");
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (_module is null)
        {
            return;
        }

        var module = await _module;
        if (_held)
        {
            _held = false;
            await module.InvokeVoidAsync("release");
        }

        await module.DisposeAsync();
    }
}
