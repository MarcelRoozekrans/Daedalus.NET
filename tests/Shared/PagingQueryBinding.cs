using System.Reflection;
using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Daedalus.Tests.Shared;

/// <summary>
///     Binds the <c>page</c> and <c>pageSize</c> query parameters of a paged endpoint as the request delegate does,
///     through <see cref="IBindableFromHttpContext{TSelf}"/> over a real <see cref="ParameterInfo"/> so the binder reads
///     the parameter's name. Linked into each test project that calls a paged handler directly.
/// </summary>
internal static class PagingQueryBinding
{
    /// <summary>Binds both parameters from <paramref name="queryString"/>, for example <c>?page=2&amp;pageSize=5</c>.</summary>
    public static async Task<(ApiQuery<int> Page, ApiQuery<int> PageSize)> BindAsync(HttpContext context, string queryString)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Request.QueryString = new QueryString(queryString);
        var parameters = typeof(PagingQueryBinding)
            .GetMethod(nameof(PagingParameters), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        return (await Bind<ApiQuery<int>>(context, parameters[0]), await Bind<ApiQuery<int>>(context, parameters[1]));
    }

    private static void PagingParameters(ApiQuery<int> page, ApiQuery<int> pageSize) => _ = (page, pageSize);

    private static async Task<T> Bind<T>(HttpContext context, ParameterInfo parameter)
        where T : class, IBindableFromHttpContext<T> => (await T.BindAsync(context, parameter))!;
}
