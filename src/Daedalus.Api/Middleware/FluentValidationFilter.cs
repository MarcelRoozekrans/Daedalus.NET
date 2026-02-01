using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Daedalus.Api.Middleware;

/// <summary>
///     ASP.NET Core action filter that runs FluentValidation validators
///     on action parameters before the action executes.
///     Returns a 400 Bad Request with RFC 7807 ProblemDetails on validation failure.
/// </summary>
public sealed class FluentValidationFilter(IServiceProvider serviceProvider) : IAsyncActionFilter
{
    [UnconditionalSuppressMessage("Trimming", "IL3050",
        Justification = "MakeGenericType is required for validator discovery. AOT will preserve IValidator<T> types.")]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var argumentType = argument.GetType();
            var validatorType = typeof(IValidator<>).MakeGenericType(argumentType);
            var validator = serviceProvider.GetService(validatorType);

            if (validator is null)
            {
                continue;
            }

            var validationContext = new ValidationContext<object>(argument);
            var validationResult = await ((IValidator)validator).ValidateAsync(validationContext, context.HttpContext.RequestAborted);

            if (!validationResult.IsValid)
            {
                var errors = validationResult.Errors
                    .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray(),
                        StringComparer.Ordinal);

                var problemDetails = new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
                };

                context.Result = new BadRequestObjectResult(problemDetails);
                return;
            }
        }

        await next();
    }
}
