using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Infrastructure.Services.CodeAnalysis;

/// <summary>Phase 2.11, ruling P30: a token failure's text reaches a run's error, so it carries no exception's text.</summary>
public sealed class RepositoryAuthenticationProviderFailureTests
{
    /// <summary>Red: return <c>$"Error getting auth token: {ex.Message}"</c> as before; the text carries the exception's message.</summary>
    [Fact]
    public void A_failure_to_read_the_token_names_the_platform_and_no_exception_text()
    {
        var result = RepositoryAuthenticationProvider.ReadToken(
            RepositoryPlatform.AzureDevOps, _ => throw new InvalidOperationException("zqx-exception-text"), NullLogger.Instance);

        result.Error.Should().Be("Error getting the auth token for AzureDevOps");
    }

    /// <summary>
    ///     Logs are exported over OTLP, so the log carries the exception's type and never its message or stack.
    ///     Red: pass <c>ex</c> to the log call again, as before; a line carries the exception's text.
    ///     Red: log <c>ex.Message</c> as the exception type; a line carries the exception's text.
    ///     Red: drop the log call; the type line assertion fails.
    /// </summary>
    [Fact]
    public void A_failure_to_read_the_token_logs_the_exception_type_and_not_its_text()
    {
        var logger = new CapturingLogger();

        RepositoryAuthenticationProvider.ReadToken(
            RepositoryPlatform.AzureDevOps, _ => throw new InvalidOperationException("zqx-exception-text"), logger);

        logger.Lines.Should().Contain(line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
        logger.Lines.Should().NotContain(line => line.Contains("zqx-exception-text", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The text of a failure to configure credentials reaches a run's error, so it names the platform and carries no
    ///     exception text. The failure is made by a logger that throws on the success line.
    ///     Red: return <c>$"Error configuring credentials: {ex.Message}"</c> as before; the text carries the exception's message.
    /// </summary>
    [Fact]
    public async Task A_failure_to_configure_credentials_returns_text_without_the_exception_message()
    {
        Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", "token-for-test");
        try
        {
            var provider = new RepositoryAuthenticationProvider(new ThrowingOnInformationLogger());

            var result = await provider.ConfigureGitCredentialsAsync(RepositoryPlatform.AzureDevOps, "/tmp/work", CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Be("Error configuring credentials for AzureDevOps");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", null);
        }
    }

    /// <summary>
    ///     Logs are exported over OTLP, so the log carries the exception's type and never its message or stack.
    ///     Red: log <c>ex.Message</c> as the exception type; a line carries the exception's text.
    ///     Red: drop the log call; the type line assertion fails.
    /// </summary>
    [Fact]
    public async Task A_failure_to_configure_credentials_logs_the_exception_type_and_not_its_text()
    {
        Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", "token-for-test");
        try
        {
            var logger = new ThrowingOnInformationLogger();
            var provider = new RepositoryAuthenticationProvider(logger);

            await provider.ConfigureGitCredentialsAsync(RepositoryPlatform.AzureDevOps, "/tmp/work", CancellationToken.None);

            logger.Lines.Should().Contain(line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
            logger.Lines.Should().NotContain(line => line.Contains("zqx-exception-text", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_DEVOPS_TOKEN", null);
        }
    }

    /// <summary>A capturing logger that throws when the success line is written, which is inside the method's try block.</summary>
    private sealed class ThrowingOnInformationLogger : ILogger<RepositoryAuthenticationProvider>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                throw new InvalidOperationException("zqx-exception-text");

            _lines.Enqueue(formatter(state, exception));
            if (exception is not null)
                _lines.Enqueue(exception.ToString());
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                    _lines.Enqueue(value.Value?.ToString() ?? "");
            }
        }
    }

    /// <summary>Every formatted message, structured value and attached exception.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue(formatter(state, exception));
            if (exception is not null)
                _lines.Enqueue(exception.ToString());
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                    _lines.Enqueue(value.Value?.ToString() ?? "");
            }
        }
    }
}
