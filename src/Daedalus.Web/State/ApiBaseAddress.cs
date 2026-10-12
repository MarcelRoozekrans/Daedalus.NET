namespace Daedalus.Web.State;

/// <summary>The API's base address, which the hub connection is built from.</summary>
/// <param name="Value">
///     The absolute base address, the one the API's <see cref="HttpClient"/> has. Only its origin counts: the hub
///     path, like every <see cref="ApiClient"/> route, is root-relative, so any path the base carries is ignored by
///     both alike.
/// </param>
public sealed record ApiBaseAddress(Uri Value);
