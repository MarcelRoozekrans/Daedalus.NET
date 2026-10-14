using System.Collections.Frozen;

namespace Daedalus.Agents.Decisions;

/// <summary>
///     The review lens pins <see cref="Daedalus.Agents.Workflow.ReviewLensRunner"/> applies: <c>Thalos:Decisions:PinnedLensModels</c>
///     when decisions are enabled, by the same <see cref="DecisionsConfig.IsEnabled"/> that registers the decision client,
///     and none otherwise.
/// </summary>
/// <remarks>
///     A pin is part of the decided setup, not of today's: with decisions off every node runs as it did before phase 2.10,
///     its default model and full tool list, and a review lens is no exception. So configured pins are inactive while
///     decisions are off, and <see cref="DecisionsStartupLog"/> says how many.
/// </remarks>
internal sealed class ActiveLensPins
{
    private ActiveLensPins(FrozenDictionary<string, string> models) => Models = models;

    /// <summary>Lens name to the model its pass runs on; empty when decisions are off.</summary>
    public FrozenDictionary<string, string> Models { get; }

    /// <summary>The pins that apply for <paramref name="config"/> and the configured key.</summary>
    public static ActiveLensPins For(DecisionsConfig config, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        var pins = config.IsEnabled(apiKey) ? config.PinnedLensModels : new Dictionary<string, string>(StringComparer.Ordinal);
        return new ActiveLensPins(pins.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
