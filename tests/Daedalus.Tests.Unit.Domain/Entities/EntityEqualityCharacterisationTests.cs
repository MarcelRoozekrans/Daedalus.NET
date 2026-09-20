namespace Daedalus.Tests.Unit.Domain.Entities;

/// <summary>
///     Characterises identity-based equality of <c>Daedalus.Domain.Entities.Entity&lt;TId&gt;</c> —
///     the ACTUAL base class the 9 domain entities (<c>AgentMemory</c>, <c>AgentMessage</c>,
///     <c>BrainstormMessage</c>, <c>ChannelConversation</c>, <c>ScheduledRun</c>,
///     <c>ScheduledRunExecution</c>, <c>Skill</c>, <c>TaskExecution</c>, <c>AnalysisIteration</c>)
///     derive from today.
/// </summary>
/// <remarks>
///     <para>
///     IMPORTANT CORRECTION TO THE TASK BRIEF: the brief's contract was taken from CSFE 3.6.0 by
///     reflection on the assumption that the 9 entities derive from
///     <c>CSharpFunctionalExtensions.Entity&lt;TId&gt;</c>. Verified by loading the built
///     <c>Daedalus.Domain.dll</c> and reflecting on <c>ScheduledRun.BaseType</c>: the real base type is
///     <c>Daedalus.Domain.Entities.Entity`1[Guid]</c>, an assembly-local class that has existed since
///     the repository's initial commit (<c>src/Daedalus.Domain/Entities/Entity.cs</c>). CSFE is still
///     imported in these entity files (<c>using CSharpFunctionalExtensions;</c>) only for
///     <c>Result&lt;T&gt;</c>, which name resolution never routes to <c>Entity&lt;TId&gt;</c> because the
///     local type in the same namespace shadows the CSFE one. CSFE's <c>Entity&lt;TId&gt;</c> is
///     effectively dead code as far as these 9 entities are concerned. Task 8 is not swapping a CSFE
///     base class for a hand-rolled one — the hand-rolled one already exists and Task 8 REWRITES it.
///     </para>
///     <para>
///     Two of the pinned behaviours below are the OPPOSITE of what the brief predicted, because the
///     current, real implementation is weaker than CSFE's contract in ways relevant to EF Core change
///     tracking (see the per-test remarks). These are reported, not silently fixed — production code is
///     unchanged. Task 8's proposed replacement (shown in its own brief) deliberately closes both gaps;
///     that makes it a behaviour CHANGE relative to today, not a pure refactor, and the two tests named
///     below are expected to need conscious, documented updates when Task 8 lands, not silent breakage.
///     </para>
///     <para>
///     "CompareTo orders by Id" from the brief is omitted entirely: the real
///     <c>Daedalus.Domain.Entities.Entity&lt;TId&gt;</c> implements neither <c>IComparable</c> nor
///     <c>IComparable&lt;Entity&lt;TId&gt;&gt;</c> today, so there is no current behaviour to
///     characterise — the brief's sample test would not compile against the real class. This is net-new
///     capability Task 8 introduces, not something Task 7 can pin.
///     </para>
/// </remarks>
public sealed class EntityEqualityCharacterisationTests
{
    private sealed class TestEntity : Daedalus.Domain.Entities.Entity<Guid>
    {
        public TestEntity(Guid id) => Id = id;
        public TestEntity() { }
    }

    private sealed class OtherEntity : Daedalus.Domain.Entities.Entity<Guid>
    {
        public OtherEntity(Guid id) => Id = id;
    }

    private sealed class StringEntity : Daedalus.Domain.Entities.Entity<string>
    {
        public StringEntity(string id) => Id = id;
    }

    /// <summary>
    ///     PRE-EXISTING BEHAVIOUR, NOT WHAT THE BRIEF PREDICTED. The real <c>Equals</c> is
    ///     <c>obj is Entity&lt;TId&gt; entity &amp;&amp; Id.Equals(entity.Id)</c> — it never special-cases
    ///     <c>default(TId)</c>. Two transient <see cref="TestEntity"/> instances both have
    ///     <c>Id == Guid.Empty</c>, so they compare equal by value today, including via <c>==</c> and with
    ///     matching hash codes. This is exactly the "two transients collide" defect class the brief warned
    ///     a bad replacement might introduce; the real class already has it. Would break if: a fix treats
    ///     <c>default(TId)</c> as "no identity yet" the way Task 8's proposed replacement does.
    /// </summary>
    [Fact]
    public void Two_transient_entities_of_the_same_type_compare_equal_today()
    {
        var a = new TestEntity();
        var b = new TestEntity();

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>Would break if: Equals stops treating same-Id instances as equal, or the == operator diverges from Equals.</summary>
    [Fact]
    public void Two_entities_of_the_same_type_with_the_same_id_are_equal()
    {
        var id = Guid.NewGuid();

        Assert.Equal(new TestEntity(id), new TestEntity(id));
        Assert.True(new TestEntity(id) == new TestEntity(id));
        Assert.Equal(new TestEntity(id).GetHashCode(), new TestEntity(id).GetHashCode());
    }

    /// <summary>
    ///     PRE-EXISTING BEHAVIOUR, NOT WHAT THE BRIEF PREDICTED. <c>Equals</c> checks
    ///     <c>obj is Entity&lt;TId&gt;</c> — any subtype sharing the same <c>TId</c> passes that check, so
    ///     a <see cref="TestEntity"/> and an unrelated <see cref="OtherEntity"/> with the same
    ///     <see cref="Guid"/> compare equal today. This is the second classic "loose Entity equality"
    ///     defect the brief warned about; the real class already has it. Would break if: a fix compares
    ///     concrete runtime types (accounting for EF Core proxies) the way Task 8's proposed replacement
    ///     does.
    /// </summary>
    [Fact]
    public void Entities_of_different_types_with_the_same_id_compare_equal_today()
    {
        var id = Guid.NewGuid();

        Assert.True(new TestEntity(id).Equals(new OtherEntity(id)));
        Assert.Equal(new TestEntity(id).GetHashCode(), new OtherEntity(id).GetHashCode());
    }

    /// <summary>Would break if: Equals starts throwing or returning true for a null comparand, or either operator stops short-circuiting on a null side.</summary>
#pragma warning disable CA1508 // The comparand is deliberately a literal null; flow analysis correctly knows `e` is non-null, but the point of this test is to pin the null-handling branch of Equals/== itself.
    [Fact]
    public void An_entity_is_not_equal_to_null_on_either_side()
    {
        var e = new TestEntity(Guid.NewGuid());

        Assert.False(e.Equals(null));
        Assert.False(e == null);
        Assert.True(e != null);
        Assert.False(null == e);
        Assert.True(null != e);
    }
#pragma warning restore CA1508

    /// <summary>Would break if: the generic implementation stops working for a reference-typed TId (e.g. a hardcoded Guid assumption), matching the brief's concern about Skill : Entity&lt;string&gt;.</summary>
    [Fact]
    public void String_keyed_entities_behave_identically_for_equal_and_distinct_ids()
    {
        Assert.Equal(new StringEntity("a"), new StringEntity("a"));
        Assert.NotEqual(new StringEntity("a"), new StringEntity("b"));
    }
}
