namespace Daedalus.Api.Endpoints;

/// <summary>
///     Metadata on every endpoint mapped through <see cref="DaedalusEndpoints.MapApiGroup"/>, so a test can tell an
///     endpoint that carries the endpoints' filters from one mapped on a bare group.
/// </summary>
public sealed class DaedalusEndpointGroup
{
    private DaedalusEndpointGroup()
    {
    }

    /// <summary>The one instance.</summary>
    public static DaedalusEndpointGroup Instance { get; } = new();
}
