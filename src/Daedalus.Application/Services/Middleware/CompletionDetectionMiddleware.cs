using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Application.Services.Middleware;

/// <summary>
///     Checks whether the LLM response contains the completion promise.
///     Order: 300.
/// </summary>
public sealed class CompletionDetectionMiddleware : IRalphLoopMiddleware
{
    public int Order => 300;

    public async Task<Result> InvokeAsync(
        RalphIterationContext context,
        Func<Task<Result>> continuation,
        CancellationToken ct)
    {
        if (context.LlmInvocationSucceeded &&
            !string.IsNullOrEmpty(context.LlmResponse) &&
            !string.IsNullOrEmpty(context.Task.CompletionPromise))
        {
            context.CompletionPromiseFound = context.LlmResponse
                .Contains(context.Task.CompletionPromise, StringComparison.OrdinalIgnoreCase);
        }

        return await continuation();
    }
}
