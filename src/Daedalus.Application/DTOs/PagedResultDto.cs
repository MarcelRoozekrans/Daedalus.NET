namespace Daedalus.Application.DTOs;

/// <summary>DTO for query results with pagination.</summary>
public record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize);
