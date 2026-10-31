using Microsoft.AspNetCore.Http.Extensions;

namespace Daedalus.Api.Endpoints;

/// <summary>The <c>Location</c> header MVC's <c>CreatedAtAction</c> writes, for the endpoints' 201s (ruling R21).</summary>
public static class ApiLocation
{
    /// <summary>
    ///     The absolute URL of <paramref name="path"/>: the request's scheme and host, then its path base, then the
    ///     path, as MVC's link generator builds it. <paramref name="path"/> keeps the casing the controller's route
    ///     gave, for example <c>/api/Tasks/{id}</c> for <c>api/[controller]</c>.
    /// </summary>
    public static string For(HttpRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(path);

        return UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, new PathString(path));
    }
}
