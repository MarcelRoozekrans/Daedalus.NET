using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Tests.Playwright.Browser;

internal class StubContext7DocumentationInjector : IContext7DocumentationInjector
{
    public Task<Result<string>> GetDocumentationContextAsync(string prompt, CancellationToken ct) =>
        Task.FromResult(Result.Success(string.Empty));
}
