namespace Daedalus.Web.Services;

public record CreateProjectDto(
    string ProjectName,
    string Description,
    string Version);
