using Daedalus.Agents;
using Daedalus.Agents.Security;
using Daedalus.Agents.Workflow;
using Thalos.Workflow;
using ZeroAlloc.Authorization;

namespace Daedalus.Tests.Unit.Security;

/// <summary>
///     Task B9: <c>csharp-write</c>, bound to <c>roslyn__apply_*</c>. A granted workflow caller passes only when its grant
///     includes <c>.cs</c>, because RoslynCodeLens writes <c>.cs</c> documents with no extension check of its own. Every
///     workflow caller here is a real <see cref="WorkflowCaller"/>, so its roles and claim are built as production builds
///     them.
/// </summary>
public sealed class CSharpWritePolicyTests
{
    private static readonly CSharpWritePolicy Policy = new();

    /// <summary>
    ///     Red for the <c>.md</c> row: pass every <c>workspace-writer</c> caller, as <c>workspace-write</c> does. Red for
    ///     the <c>.mdx;.csx</c> row: match the extension as a substring of the claim rather than as a list entry.
    /// </summary>
    [Theory]
    [InlineData(".md")]
    [InlineData(".mdx", ".csx")]
    public async Task A_granted_caller_whose_grant_lacks_cs_is_denied(params string[] extensions)
    {
        var result = await Policy.EvaluateAsync(Granted(extensions), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     Red for both rows: deny every <c>workspace-writer</c> caller. Red for the second row: read only the claim's
    ///     first entry. The second row's <c>.CS</c> is lower-cased by <see cref="WorkflowCaller"/>, as configured
    ///     grants always are.
    /// </summary>
    [Theory]
    [InlineData(".cs", ".md")]
    [InlineData(".md", ".CS")]
    public async Task A_granted_caller_whose_grant_includes_cs_passes(params string[] extensions)
    {
        var result = await Policy.EvaluateAsync(Granted(extensions), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>An ungranted workflow caller holds only <c>workflow</c>. Red: pass any caller with a run claim.</summary>
    [Fact]
    public async Task An_ungranted_workflow_caller_is_denied()
    {
        var result = await Policy.EvaluateAsync(new WorkflowCaller(Run(), grant: null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     A workspace writer with no extension claim can only be a caller built outside <see cref="WorkflowCaller"/>. The
    ///     <c>workspace__*</c> tools read an absent claim as "ceiling only"; this policy reads it as no grant. Red: treat
    ///     an absent claim as a pass.
    /// </summary>
    [Fact]
    public async Task A_workspace_writer_with_no_extension_claim_is_denied()
    {
        var caller = Substitute.For<ISecurityContext>();
        caller.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { WorkspaceWritePolicy.WorkspaceWriterRole });
        caller.Claims.Returns(new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await Policy.EvaluateAsync(caller, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     A human's chat turn keeps <c>roslyn__apply_*</c> against the host's own solution, as the <c>developer</c>
    ///     binding allowed before. Red: remove the developer and admin pass.
    /// </summary>
    [Theory]
    [InlineData("developer")]
    [InlineData("admin")]
    public async Task A_developer_or_admin_passes(string role)
    {
        var caller = Substitute.For<ISecurityContext>();
        caller.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { role });
        caller.Claims.Returns(new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await Policy.EvaluateAsync(caller, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: add <c>reader</c> to the roles that pass.</summary>
    [Fact]
    public async Task A_scheduled_reader_is_denied()
    {
        var caller = Substitute.For<ISecurityContext>();
        caller.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { "reader" });
        caller.Claims.Returns(new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await Policy.EvaluateAsync(caller, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    private static WorkflowCaller Granted(string[] extensions)
    {
        var grant = new WriteGrantConfig { Process = "manufacture", Node = "implement" };
        foreach (var extension in extensions)
        {
            grant.AllowedExtensions.Add(extension);
        }

        return new WorkflowCaller(Run(), grant);
    }

    private static WorkflowRun Run() => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 1,
        CurrentNode = "implement",
        CurrentSeq = 0,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };
}
