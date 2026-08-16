namespace Daedalus.Api.Controllers;

/// <summary>
///     Response model for test connection endpoint
/// </summary>
public record TestConnectionResponse
{
    public bool Connected { get; init; }
    public string Message { get; init; } = string.Empty;
}
