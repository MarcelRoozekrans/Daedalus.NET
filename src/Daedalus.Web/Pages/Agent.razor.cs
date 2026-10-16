using Microsoft.JSInterop;

namespace Daedalus.Web.Pages;

/// <summary>Code-behind for <c>Agent.razor</c>: the JS-interop teardown. The transcript records live in <c>State/Agent</c>.</summary>
public partial class Agent
{
    /// <summary>Releases the Escape listener and the module; JS errors during teardown (e.g. circuit gone) are swallowed.</summary>
    private async ValueTask DisposeInteropAsync()
    {
        try
        {
            if (_escapeHandle is not null)
            {
                await _escapeHandle.InvokeVoidAsync("dispose");
                await _escapeHandle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSException)
        {
            // Nothing left to clean up on the JS side.
        }
        catch (JSDisconnectedException)
        {
            // Browser already navigated away.
        }
        finally
        {
            _selfRef?.Dispose();
        }
    }
}
