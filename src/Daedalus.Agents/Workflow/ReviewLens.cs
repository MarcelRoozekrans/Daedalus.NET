using System.Collections.Immutable;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     One pass of the manufacturing review rubric: a name a process node declares under <c>lenses:</c>, the
///     question that pass asks, and the defect class it exists to catch.
/// </summary>
/// <remarks>
///     The three lenses are not generic review advice. Each one names a defect class this repository has
///     actually shipped, which is the whole argument for these three rather than some other three — a rubric
///     whose criteria look arbitrary gets skimmed. <see cref="DefectClass"/> carries that citation into the
///     prompt the reviewer is given, so the reason travels with the instruction instead of living only in a
///     design document the agent never reads.
///     <para>
///     <b>Order is meaningful.</b> <see cref="ReviewLensRunner"/> runs the declared lenses in declared order and
///     stops at the first rejection, so which lens is cheapest to fail against is a real choice, not an
///     alphabetical accident. Correctness is first because a change that does not do what was asked makes the
///     other two questions moot.
///     </para>
/// </remarks>
/// <param name="Name">The identifier a process node's <c>lenses:</c> list names this pass by.</param>
/// <param name="Asks">The question this pass puts to the change, given to the reviewer verbatim.</param>
/// <param name="DefectClass">The defect this project has shipped that this lens exists to catch.</param>
public sealed record ReviewLens(string Name, string Asks, string DefectClass)
{
    /// <summary>Does the change do what was asked? Name the input, state or ordering that breaks it.</summary>
    public static readonly ReviewLens Correctness = new(
        "correctness",
        "Does the change do what was asked? Name the concrete input, state or ordering that breaks it. " +
        "\"Looks fine\" is not a finding and neither is a restatement of the diff.",
        "Phase 2.2's StartAsync never enqueued its first dispatch. It compiled, it passed, and the pipeline " +
        "it started never moved.");

    /// <summary>For each test or control added, what code change turns it red? If nothing does, say so.</summary>
    public static readonly ReviewLens Falsifiability = new(
        "falsifiability",
        "For each test or control the change adds, name the code change that would turn it red. If you cannot " +
        "name one, the assertion cannot fail and is not a control - say so as a finding.",
        "Phase 1.7 alone found eight tests that passed for the wrong reason, including two guards that could " +
        "not fail by construction. Phase 2.2 Part B shipped four fixes with no guard at all.");

    /// <summary>For each claim in a comment, doc or name, is the thing it credits the thing that enforces it?</summary>
    public static readonly ReviewLens Mechanism = new(
        "mechanism",
        "For each claim made by a comment, document or identifier in the change: is the thing it credits the " +
        "thing that actually enforces the behaviour? A tool that is absent from an agent's list is not a tool " +
        "denied by policy, and a policy that matches no registered tool protects nothing.",
        "Phase 2.2 shipped nine instances of prose crediting the wrong mechanism. Phase 2.3 found four more, " +
        "two of them inside its own design document.");

    /// <summary>The three lenses in the order <c>processes/manufacture.yaml</c> declares them.</summary>
    public static readonly ImmutableArray<ReviewLens> All = [Correctness, Falsifiability, Mechanism];

    /// <summary>
    ///     Resolves the names a process node declared under <c>lenses:</c> to the lenses that will be run, in the
    ///     declared order. An empty or absent declaration resolves to an empty sequence — that is a node with no
    ///     lens rubric, not an error, and <see cref="ReviewLensRunner"/> leaves such a node alone.
    /// </summary>
    /// <remarks>
    ///     A name this type does not know is a <em>failure</em>, never a silently skipped pass. A typo in
    ///     <c>lenses:</c> that quietly dropped one of three passes would make a two-lens approval indistinguishable
    ///     from a three-lens one, which is precisely the invisible weakening this rubric exists to prevent.
    /// </remarks>
    /// <param name="declared">The names from the node's <c>lenses:</c> list, in document order.</param>
    public static Result<ImmutableArray<ReviewLens>> Resolve(IReadOnlyList<string>? declared)
    {
        if (declared is null || declared.Count == 0)
            return Result<ImmutableArray<ReviewLens>>.Success([]);

        var resolved = ImmutableArray.CreateBuilder<ReviewLens>(declared.Count);
        foreach (var name in declared)
        {
            var lens = All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (lens is null)
            {
                return Result<ImmutableArray<ReviewLens>>.Failure(
                    $"Review lens '{name}' is not a lens this host knows. Known lenses: {string.Join(", ", All.Select(l => l.Name))}.");
            }

            resolved.Add(lens);
        }

        return Result<ImmutableArray<ReviewLens>>.Success(resolved.ToImmutable());
    }
}
