using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Infrastructure.Services.NoOp;

/// <summary>
///     No-op embedding service used when Ollama is unavailable.
///     Returns failure for all requests, triggering keyword search fallback.
/// </summary>
public sealed class NoOpEmbeddingService : IEmbeddingService
{
    public bool IsAvailable => false;

    public Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(
            Result.Failure<float[]>("Embedding service unavailable — using keyword search fallback"));
    }
}
