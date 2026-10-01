using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Unit tests for <see cref="WorkflowRunRecord.Create"/>: every field a column could not hold is a validation
///     error, never a database failure at insert time.
/// </summary>
public sealed class WorkflowRunRecordTests
{
    private static readonly Guid _runId = new(0x7d4c0c8e, 0x3f55, 0x4f0e, 0x9b, 0x8a, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x6b);
    private static readonly DateTime _now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Result<WorkflowRunRecord> Create(
        Guid? runId = null,
        string node = "implement",
        string kind = WorkflowRunRecord.WorkspaceWriteKind,
        string principalId = "workflow:run/implement",
        string? startedById = "u-admin",
        string payloadJson = """{"tool":"workspace__write_file","path":"src/A.cs"}""",
        DateTime? createdAt = null) =>
        WorkflowRunRecord.Create(
            runId ?? _runId, 3, node, kind, principalId, startedById, payloadJson, createdAt ?? _now);

    /// <summary>
    ///     Asserts <paramref name="result"/> failed, then returns its error. Asserting first turns a guard that
    ///     stopped rejecting into an assertion failure, not an exception from reading a success's error.
    /// </summary>
    private static string Rejected(Result<WorkflowRunRecord> result)
    {
        result.IsFailure.Should().BeTrue();
        return result.Error;
    }

    [Fact]
    public void Create_keeps_every_field()
    {
        var record = Create().Value;

        record.RunId.Should().Be(_runId);
        record.Seq.Should().Be(3);
        record.Node.Should().Be("implement");
        record.Kind.Should().Be("workspace-write");
        record.PrincipalId.Should().Be("workflow:run/implement");
        record.StartedById.Should().Be("u-admin");
        record.PayloadJson.Should().Be("""{"tool":"workspace__write_file","path":"src/A.cs"}""");
        record.CreatedAt.Should().Be(_now);
    }

    [Fact]
    public void Create_accepts_a_run_with_no_starter() =>
        Create(startedById: null).Value.StartedById.Should().BeNull();

    [Fact]
    public void Create_rejects_an_empty_run_id() =>
        Rejected(Create(runId: Guid.Empty)).Should().Be("Run id is required.");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_node(string node) =>
        Rejected(Create(node: node)).Should().Be("Node is required.");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_kind(string kind) =>
        Rejected(Create(kind: kind)).Should().Be("Kind is required.");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_principal(string principalId) =>
        Rejected(Create(principalId: principalId)).Should().Be("Principal id is required.");

    [Fact]
    public void Create_rejects_a_blank_but_present_starter() =>
        Rejected(Create(startedById: " ")).Should().Be("Started-by id must be null or non-blank.");

    [Fact]
    public void Node_may_be_128_chars_but_not_129()
    {
        Create(node: new string('n', 128)).IsSuccess.Should().BeTrue();
        Rejected(Create(node: new string('n', 129))).Should().Be("Node must be at most 128 characters.");
    }

    [Fact]
    public void Kind_may_be_32_chars_but_not_33()
    {
        Create(kind: new string('k', 32)).IsSuccess.Should().BeTrue();
        Rejected(Create(kind: new string('k', 33))).Should().Be("Kind must be at most 32 characters.");
    }

    [Fact]
    public void Principal_may_be_256_chars_but_not_257()
    {
        Create(principalId: new string('p', 256)).IsSuccess.Should().BeTrue();
        Rejected(Create(principalId: new string('p', 257))).Should().Be("Principal id must be at most 256 characters.");
    }

    [Fact]
    public void Starter_may_be_256_chars_but_not_257()
    {
        Create(startedById: new string('s', 256)).IsSuccess.Should().BeTrue();
        Rejected(Create(startedById: new string('s', 257))).Should().Be("Started-by id must be at most 256 characters.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"path":""")]
    [InlineData("""{"a":1} {"b":2}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":"\u0000"}""")]
    [InlineData("""{"\u0000":1}""")]
    public void Create_rejects_a_payload_jsonb_would_refuse(string payloadJson) =>
        Rejected(Create(payloadJson: payloadJson)).Should().Be("Payload must be valid JSON.");

    [Theory]
    [InlineData("""{"a":"\\u0000"}""")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void Create_accepts_any_single_json_value_jsonb_holds(string payloadJson) =>
        Create(payloadJson: payloadJson).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_rejects_a_time_that_is_not_utc(DateTimeKind kind) =>
        Rejected(Create(createdAt: DateTime.SpecifyKind(_now, kind))).Should().Be("Created at must be a UTC time.");
}
