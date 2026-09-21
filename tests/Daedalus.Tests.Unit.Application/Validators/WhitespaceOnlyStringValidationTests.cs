using Daedalus.Application.DTOs;
using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.Validation;

namespace Daedalus.Tests.Unit.Application.Validators;

/// <summary>
///     Regression coverage for the whitespace-only-string defect fixed in phase 1.7: ZeroAlloc.Validation's
///     <c>[NotEmpty]</c> lowers to <c>string.IsNullOrEmpty</c>, which treats a whitespace-only string as present,
///     unlike FluentValidation's <c>NotEmpty()</c>, which rejects it. Every DTO touched by the fix is represented
///     by at least one required-string property here, plus a dedicated case for
///     <see cref="CreateRepositoryConfigurationDto.Platform"/>, whose separate platform-membership check means
///     whitespace must still produce two messages, not one, in the original order.
/// </summary>
public class WhitespaceOnlyStringValidationTests
{
    public static TheoryData<Func<ValidationResult>, string, string[]> SingleMessageCases => new()
    {
        {
            () => new CreateProjectDtoValidator().Validate(new CreateProjectDto("   ", "Valid description", "1.0.0")),
            nameof(CreateProjectDto.ProjectName),
            ["Project name is required."]
        },
        {
            () => new UpdateProjectDtoValidator().Validate(new UpdateProjectDto("   ", "Valid description", "1.0.0")),
            nameof(UpdateProjectDto.ProjectName),
            ["Project name is required."]
        },
        {
            () => new CreateTaskDtoValidator().Validate(new CreateTaskDto(
                Guid.NewGuid(), null, "   ", "Valid description", 0, null, 1, 0, "Valid prompt", "Valid promise", 5,
                null, null)),
            nameof(CreateTaskDto.Title),
            ["Title is required."]
        },
        {
            () => new CreateRepositoryConfigurationDtoValidator().Validate(new CreateRepositoryConfigurationDto
            {
                Name = "   ", Url = "https://example.com/repo.git", Platform = "GitHub", DefaultBranch = "main"
            }),
            nameof(CreateRepositoryConfigurationDto.Name),
            ["Repository name is required."]
        },
        {
            () => new UpdateRepositoryConfigurationDtoValidator().Validate(new UpdateRepositoryConfigurationDto
            {
                Name = "   ", Url = "https://example.com/repo.git", DefaultBranch = "main"
            }),
            nameof(UpdateRepositoryConfigurationDto.Name),
            ["Repository name is required."]
        },
        {
            () => new SendBrainstormMessageDtoValidator().Validate(new SendBrainstormMessageDto("   ")),
            nameof(SendBrainstormMessageDto.Content),
            ["Message content is required."]
        },
        {
            () => new SubmitAnalysisRequestValidator().Validate(new SubmitAnalysisRequest(
                "https://example.com/repo.git", "src/File.cs", AnalysisType.Refactor, "   ", "Valid description",
                [])),
            nameof(SubmitAnalysisRequest.Title),
            ["Title is required."]
        }
    };

    [Theory]
    [MemberData(nameof(SingleMessageCases))]
    public void Validate_WithWhitespaceOnlyRequiredString_ShouldFailWithExactlyThatMessage(
        Func<ValidationResult> validate, string propertyName, string[] expectedMessages)
    {
        var result = validate();
        GetMessages(result, propertyName).Should().Equal(expectedMessages);
    }

    /// <summary>
    ///     Pins the fix for the reviewer's residual finding: <c>Platform</c> was originally left on
    ///     <c>[NotEmpty]</c> because its separate <c>[Must(IsValidPlatform)]</c> check also rejects whitespace —
    ///     but that changed the message count from two (FluentValidation) to one (the unfixed port), which is an
    ///     externally-visible response-body change. This test fails if <c>Platform</c>'s presence check regresses
    ///     to <c>[NotEmpty]</c> (only one message would be produced, or in the wrong order).
    /// </summary>
    [Fact]
    public void Validate_WithWhitespaceOnlyPlatform_ShouldFailWithBothMessagesInOriginalOrder()
    {
        var dto = new CreateRepositoryConfigurationDto
        {
            Name = "Valid name", Url = "https://example.com/repo.git", Platform = "   ", DefaultBranch = "main"
        };
        var result = new CreateRepositoryConfigurationDtoValidator().Validate(dto);

        GetMessages(result, nameof(CreateRepositoryConfigurationDto.Platform)).Should().Equal(
            "Platform is required.",
            "Platform must be one of: GitHub, GitLab, AzureDevOps, Bitbucket, Gitea.");
    }

    private static List<string> GetMessages(ValidationResult result, string propertyName)
    {
        var messages = new List<string>();
        foreach (ref readonly var failure in result.Failures)
        {
            if (string.Equals(failure.PropertyName, propertyName, StringComparison.Ordinal))
            {
                messages.Add(failure.ErrorMessage);
            }
        }

        return messages;
    }
}
