using Thalos;

namespace Daedalus.Agents;

/// <summary>
///     Validates that every name in a configured collection resolves to a configured <see cref="AgentDefinition"/>
///     in <see cref="IAgentCatalog"/>, using the same case-insensitive comparison <c>ChannelPump</c> uses to
///     resolve <c>Thalos:Channels:DefaultAgent</c> to a definition — agent names are typed by humans on phones.
/// </summary>
/// <remarks>
///     <para>
///     Spec §7: a host must fail to start on a configuration error rather than defer the failure to the first
///     time something needs the name (for <c>DefaultAgent</c>, that is the first message a channel routes).
///     </para>
///     <para>
///     Takes a collection rather than a single name on purpose. The caller,
///     <c>ScheduleReconcilerHostedService</c>, already passes more than one source in the same collection:
///     <c>Thalos:Channels:DefaultAgent</c> (when configured) alongside every <c>RepoDigestPrompts.AgentNames</c>
///     entry — the <c>RepoDigest</c> workflow's scout and writer agent names. Validating them together means
///     every unknown name, from every source, is reported in one pass rather than one restart per name.
///     </para>
/// </remarks>
public static class AgentNameValidator
{
    /// <summary>
    ///     Checks every name in <paramref name="names"/> against <paramref name="catalog"/>'s
    ///     <see cref="IAgentCatalog.Agents"/> by <see cref="AgentDefinition.Name"/>, case-insensitively.
    /// </summary>
    /// <param name="catalog">The registered agent definitions to check names against.</param>
    /// <param name="names">The configured agent names to validate. May be empty; never throws in that case.</param>
    /// <exception cref="InvalidOperationException">
    ///     One or more names in <paramref name="names"/> do not match any <see cref="AgentDefinition.Name"/> in
    ///     <paramref name="catalog"/>. The message names every unknown name at once, not just the first — failing
    ///     on the first would mean a misconfigured host is fixed one restart at a time.
    /// </exception>
    public static void Validate(IAgentCatalog catalog, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(names);

        var unknown = names
            .Where(name => !catalog.Agents.Any(agent => string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"No agent is registered under the name {string.Join(", ", unknown)}. " +
                "Check the configured name against Thalos:Agents.");
        }
    }
}
