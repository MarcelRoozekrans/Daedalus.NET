namespace Daedalus.Web.Services;

public record UpdateProjectDto(
    string ProjectName,
    string Description,
    string Version);
