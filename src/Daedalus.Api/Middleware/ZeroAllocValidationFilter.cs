using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Daedalus.Api.Middleware;

/// <summary>
///     Runs source-generated validators on action parameters before the action executes.
///     Returns a 400 Bad Request with RFC 7807 ProblemDetails on validation failure,
///     byte-identical to the response the FluentValidation filter produced.
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

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (ref readonly var failure in validationResult.Failures)
            {
                errors[failure.PropertyName] = errors.TryGetValue(failure.PropertyName, out var existing)
                    ? [.. existing, failure.ErrorMessage]
                    : [failure.ErrorMessage];
            }

            var problemDetails = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            };

            context.Result = new BadRequestObjectResult(problemDetails);
            return;
        }

        await next();
    }
}
