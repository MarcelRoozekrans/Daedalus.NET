using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>Builds the <see cref="NodeUsageRecorder"/> a hand-built <see cref="ReviewHandoffWorkflowStore"/> needs.</summary>
internal static class TestNodeUsage
{
    /// <summary>A recorder whose scopes resolve <paramref name="records"/>, or a store that accepts every append.</summary>
    public static NodeUsageRecorder Recorder(IWorkflowRunRecordStore? records = null) =>
        new(RecordStoreScopes.For(records), TimeProvider.System, NullLogger<NodeUsageRecorder>.Instance);
}
