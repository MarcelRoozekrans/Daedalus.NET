using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Generates vector embeddings for semantic search.
///     Implementations may use Ollama, OpenAI, or a no-op fallback.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Generates an embedding vector for the given text.</summary>
    Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>Whether the embedding service is available and healthy.</summary>
    bool IsAvailable { get; }
}
