namespace Daedalus.Application.Configuration;

/// <summary>
///     How the Ralph enrichment/MCP paths recall shared learnings (<c>Thalos:Memory:RalphRecall</c>). Lives in the
///     Application layer because both sides of the port need it: <c>LearningsEnrichmentMiddleware</c> and the
///     <c>search_learnings</c> MCP tool size their requests with it, and the Thalos adapter in <c>Daedalus.Agents</c>
///     turns it into a Thalos <c>RecallOptions</c>. One class, one configuration key, so the two cannot diverge.
/// </summary>
public sealed class RalphRecallConfiguration
{
    /// <summary>Configuration section name: <c>Thalos:Memory:RalphRecall</c>.</summary>
    public const string SectionName = "Thalos:Memory:RalphRecall";

    /// <summary>Smallest number of learnings a recall may ask for.</summary>
    public const int MinTopK = 1;

    /// <summary>Largest number of learnings any recall may ask for (the adapter clamps to this).</summary>
    public const int MaxTopK = 50;

    /// <summary>Largest number of learnings the model-facing <c>search_learnings</c> tool may ask for.</summary>
    public const int MaxToolTopK = 20;

    /// <summary>Default number of learnings recalled per query (enrichment and the MCP tool default).</summary>
    public int TopK { get; set; } = 10;

    /// <summary>Minimum cosine similarity for a learning to be recalled.</summary>
    public double MinScore { get; set; } = 0.5;
}
