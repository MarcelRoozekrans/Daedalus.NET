using Daedalus.Api.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Daedalus.Api.Middleware;

/// <summary>
///     Runs source-generated validators on action parameters before the action executes.
///     Returns a 400 Bad Request with RFC 7807 ProblemDetails on validation failure,
///     byte-identical to the response the FluentValidation filter produced. The problem is built by
///     <see cref="ZeroAllocValidationEndpointFilter.ToProblem"/>, which the endpoints answer with too.
/// </summary>
public sealed class ZeroAllocValidationFilter : IAsyncActionFilter
{
    private readonly Dictionary<Type, IValidationAdapter> _adapters;

    public ZeroAllocValidationFilter(IEnumerable<IValidationAdapter> adapters)
        => _adapters = adapters.ToDictionary(a => a.TargetType);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null || !_adapters.TryGetValue(argument.GetType(), out var adapter))
            {
                continue;
            }

            var validationResult = adapter.Validate(argument);
            if (validationResult.IsValid)
            {
                continue;
            }

            context.Result = new BadRequestObjectResult(ZeroAllocValidationEndpointFilter.ToProblem(validationResult));
            return;
        }

        await next();
    }
}
