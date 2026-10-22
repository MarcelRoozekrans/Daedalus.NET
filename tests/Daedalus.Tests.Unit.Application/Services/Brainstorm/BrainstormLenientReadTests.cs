using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services.Brainstorm;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Application.Services.Brainstorm;

/// <summary>
///     Review Focus 1: the task list a model returns from the brainstorm's plan is read leniently today. An enum arrives
///     as its name, as its name in another case, or as its number, and the keys arrive in camelCase or any other case.
///     Captured through <see cref="BrainstormService.GenerateTasksAsync"/>, the one product call, before phase 3.2 moves
///     the read to a JSON context.
/// </summary>
public sealed class BrainstormLenientReadTests
{
    private readonly IBrainstormRepository _repository = Substitute.For<IBrainstormRepository>();
    private readonly IAgentFactory _agentFactory = Substitute.For<IAgentFactory>();
    private readonly IPrdService _prdService = Substitute.For<IPrdService>();

    /// <summary>
    ///     Red: drop <c>new JsonStringEnumConverter()</c> from <c>BrainstormService</c>'s parse options; the two names
    ///     fail to parse and the number still reads. Red: construct it as
    ///     <c>new JsonStringEnumConverter(allowIntegerValues: false)</c>; the number fails.
    /// </summary>
    [Theory]
    [InlineData("\"High\"", "\"Medium\"")]
    [InlineData("\"high\"", "\"mEDIUM\"")]
    [InlineData("2", "1")]
    public async Task An_enum_reads_as_its_name_any_case_or_its_number(string priority, string complexity)
    {
        var items = await ReadAsync($$"""
            [{"title":"T","description":"D","acceptanceCriteria":"A","priority":{{priority}},"complexity":{{complexity}},
              "dependencies":["X"],"phase":"P","parallelGroup":3}]
            """);

        items.Should().ContainSingle();
        items[0].Should().BeEquivalentTo(new PrdItemForConversionDto("T", "D", "A", Priority.High, Complexity.Medium, ["X"], "P", 3));
    }

    /// <summary>Red: drop <c>PropertyNameCaseInsensitive = true</c> from the parse options; no key matches and the item reads empty.</summary>
    [Fact]
    public async Task Keys_read_in_any_case()
    {
        var items = await ReadAsync("""
            [{"TITLE":"T","Description":"D","acceptancecriteria":"A","Priority":"High","COMPLEXITY":"Medium",
              "Dependencies":["X"],"phase":"P","ParallelGroup":3}]
            """);

        items.Should().ContainSingle();
        items[0].Should().BeEquivalentTo(new PrdItemForConversionDto("T", "D", "A", Priority.High, Complexity.Medium, ["X"], "P", 3));
    }

    private async Task<IReadOnlyList<PrdItemForConversionDto>> ReadAsync(string response)
    {
        var session = BrainstormSession.Create(Guid.NewGuid()).Value;
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(MessageRole.Assistant, "Done. [PHASE_COMPLETE]");
            session.AdvancePhase();
        }

        session.SetImplementationPlan("### Task 1: Create entity");
        _repository.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(Result<BrainstormSession>.Success(session));
        _repository.UpdateAsync(Arg.Any<BrainstormSession>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _agentFactory.InvokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<LlmInvocationResult>.Success(new LlmInvocationResult { Response = response, ModelId = "m" }));

        IReadOnlyList<PrdItemForConversionDto> read = [];
        _prdService.ConvertToTasksAsync(session.ProjectId, Arg.Do<IReadOnlyList<PrdItemForConversionDto>>(i => read = i), Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Success([]));

        var service = new BrainstormService(
            _repository, _agentFactory, Substitute.For<IProjectRepository>(), _prdService, NullLogger<BrainstormService>.Instance);
        var result = await service.GenerateTasksAsync(session.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        return read;
    }
}
