using System.Collections.Frozen;
using ZeroAlloc.Authorization;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     The identity a detached run executes under. Constructed from <see cref="DetachedRunOptions"/> and from the
///     principal copied onto the execution row at claim time — never from an ambient <c>ClaimsPrincipal</c>.
/// </summary>
/// <remarks>
///     A scheduled run has no human behind it. Borrowing the identity of whoever created the schedule would let a
///     run act with that person's authority hours later, after their access may have changed, with no live turn to
///     notice. Spec §4 and D7.
/// </remarks>
public sealed class DetachedPrincipal(string id, IReadOnlyList<string> roles) : ISecurityContext
{
    /// <inheritdoc />
    public string Id { get; } = id;

    /// <inheritdoc />
    public IReadOnlySet<string> Roles { get; } = roles.ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Claims { get; } = FrozenDictionary<string, string>.Empty;
}
