using Daedalus.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using ZeroAlloc.Validation;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     The endpoints' counterpart of MVC's binding and validation pipeline, in MVC's order: a body no input formatter
///     reads answers 415 (<c>UnsupportedContentTypeFilter</c>); then the binding problems of every
///     <see cref="IApiBinding"/> argument answer as one validation problem (<c>ModelStateInvalidFilter</c>); then each
///     argument with an <see cref="IValidationAdapter"/> is validated, and the first failure answers the same problem
///     <see cref="ZeroAllocValidationFilter"/> writes. <see cref="DaedalusEndpoints.MapApiGroup"/> adds it to every
///     group.
/// </summary>
internal sealed class ZeroAllocValidationEndpointFilter : IEndpointFilter
{
    private readonly Dictionary<Type, IValidationAdapter> _adapters;

    public ZeroAllocValidationEndpointFilter(IEnumerable<IValidationAdapter> adapters)
        => _adapters = adapters.ToDictionary(a => a.TargetType);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var arguments = context.Arguments;
        if (arguments.Any(argument => argument is IApiBinding { UnsupportedMediaType: true }))
        {
            return ApiProblem.Of(context.HttpContext, StatusCodes.Status415UnsupportedMediaType);
        }

        var bindingProblem = MergeBindingProblems(arguments);
        if (bindingProblem is not null)
        {
            return ApiProblem.Validation(context.HttpContext, bindingProblem);
        }

        foreach (var argument in arguments)
        {
            var value = argument is IApiBinding binding ? binding.BoundValue : argument;
            if (value is null || !_adapters.TryGetValue(value.GetType(), out var adapter))
            {
                continue;
            }

            var validationResult = adapter.Validate(value);
            if (!validationResult.IsValid)
            {
                return ApiProblem.Validation(context.HttpContext, ToProblem(validationResult));
            }
        }

        return await next(context);
    }

    /// <summary>
    ///     The problem <see cref="ZeroAllocValidationFilter"/> answers: errors by property in failure order, the first
    ///     message as <c>detail</c>, the rfc7231 400 type and no <c>traceId</c>. Both filters build it here.
    /// </summary>
    internal static ValidationProblemDetails ToProblem(ValidationResult validationResult)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (ref readonly var failure in validationResult.Failures)
        {
            errors[failure.PropertyName] = errors.TryGetValue(failure.PropertyName, out var existing)
                ? [.. existing, failure.ErrorMessage]
                : [failure.ErrorMessage];
        }

        return new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = ApiProblem.ValidationTitle,
            Detail = validationResult.Failures[0].ErrorMessage,
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
        };
    }

    /// <summary>
    ///     MVC holds every parameter's binding errors in one model state, so they answer as one problem: the first
    ///     problem's members, with every problem's errors.
    /// </summary>
    private static ValidationProblemDetails? MergeBindingProblems(IList<object?> arguments)
    {
        var problems = arguments.OfType<IApiBinding>().Select(binding => binding.Problem).OfType<ValidationProblemDetails>().ToList();
        if (problems.Count == 0)
        {
            return null;
        }

        var merged = problems[0];
        foreach (var (key, messages) in problems.Skip(1).SelectMany(problem => problem.Errors))
        {
            merged.Errors[key] = merged.Errors.TryGetValue(key, out var existing) ? [.. existing, .. messages] : messages;
        }

        return merged;
    }
}
