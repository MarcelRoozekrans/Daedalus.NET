namespace Daedalus.Api.Endpoints;

/// <summary>
///     Response model for the repository test-connection endpoint
/// </summary>
public record TestConnectionResponse
{
    public bool Connected { get; init; }
    public string Message { get; init; } = string.Empty;
}
