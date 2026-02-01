using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Daedalus.Api.Middleware;

/// <summary>
///     Global exception handler that converts unhandled exceptions into RFC 7807 ProblemDetails responses.
///     Handles specific exception types with appropriate HTTP status codes:
///     - DbUpdateConcurrencyException: 409 Conflict (optimistic locking violation)
///     - OperationCanceledException: 499 Client Closed Request (client disconnected)
///     - All others: 500 Internal Server Error
/// </summary>
public sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    [LoggerMessage(EventId = 900, Level = LogLevel.Warning,
        Message = "Concurrency conflict detected: {ExceptionMessage}")]
    private static partial void LogConcurrencyConflict(ILogger logger, string exceptionMessage);

    [LoggerMessage(EventId = 901, Level = LogLevel.Information,
        Message = "Request cancelled by client")]
    private static partial void LogRequestCancelled(ILogger logger);

    [LoggerMessage(EventId = 999, Level = LogLevel.Error,
        Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception ex);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problemDetails = exception switch
        {
            DbUpdateConcurrencyException concurrencyEx => HandleConcurrencyException(concurrencyEx),
            OperationCanceledException => HandleCancelledException(),
            _ => HandleUnexpectedException(exception, httpContext)
        };

        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";
        await System.Text.Json.JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            problemDetails,
            ApiJsonSerializerContext.Default.ProblemDetails,
            cancellationToken);

        return true;
    }

    private ProblemDetails HandleConcurrencyException(DbUpdateConcurrencyException exception)
    {
        LogConcurrencyConflict(logger, exception.Message);

        return new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Concurrency conflict",
            Detail = "The resource was modified by another operation. Please retry your request.",
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8"
        };
    }

    private ProblemDetails HandleCancelledException()
    {
        LogRequestCancelled(logger);

        return new ProblemDetails
        {
            Status = 499, // Client Closed Request (nginx convention)
            Title = "Client closed request",
            Detail = "The request was cancelled by the client.",
            Type = "https://httpstatuses.com/499"
        };
    }

    private ProblemDetails HandleUnexpectedException(Exception exception, HttpContext httpContext)
    {
        LogUnhandledException(logger, exception);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal server error",
            Detail = "An unexpected error occurred. Please try again later.",
            Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
        };

        // Include trace ID for correlation
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

        return problemDetails;
    }
}
