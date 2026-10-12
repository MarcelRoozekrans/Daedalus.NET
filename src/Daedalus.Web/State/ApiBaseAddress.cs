namespace Daedalus.Web.State;

/// <summary>The API's base address, which the hub connection is built from.</summary>
/// <param name="Value">
///     The absolute base address, the one the API's <see cref="HttpClient"/> has. A relative path resolves against it
///     as the client's requests do, so the hub sits beside the API's routes.
/// </param>
public sealed record ApiBaseAddress(Uri Value);
