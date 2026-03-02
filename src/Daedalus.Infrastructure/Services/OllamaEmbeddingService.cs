using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Generates embeddings using Ollama via Microsoft.Extensions.AI IEmbeddingGenerator.
/// </summary>
public sealed partial class OllamaEmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ILogger<OllamaEmbeddingService> logger) : IEmbeddingService
{
    private bool _available = true;

    public bool IsAvailable => _available;

    public async Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result.Failure<float[]>("Text cannot be empty");
        }

        try
        {
            var result = await embeddingGenerator
                .GenerateAsync(text, cancellationToken: ct)
                .ConfigureAwait(false);

            _available = true;
            return Result.Success(result.Vector.ToArray());
        }
        catch (Exception ex)
        {
            _available = false;
            LogEmbeddingFailed(logger, ex, text.Length);
            return Result.Failure<float[]>($"Embedding generation failed: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning,
        Message = "Embedding generation failed for text of length {TextLength}")]
    private static partial void LogEmbeddingFailed(ILogger logger, Exception exception, int textLength);
}
