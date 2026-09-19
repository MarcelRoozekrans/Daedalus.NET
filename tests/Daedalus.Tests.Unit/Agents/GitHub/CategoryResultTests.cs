using Daedalus.Agents.GitHub;

namespace Daedalus.Tests.Unit.Agents.GitHub;

public class CategoryResultTests
{
    [Fact]
    public void A_failed_category_is_not_an_empty_category()
    {
        var failed = CategoryResult<CommitSummary>.Failed("the request timed out");
        var empty = CategoryResult<CommitSummary>.Ok([], truncated: false);

        failed.Checked.Should().BeFalse();
        empty.Checked.Should().BeTrue();

        failed.Should().NotBe(empty,
            "reporting a category we could not read as if it were quiet is the failure this feature exists to avoid");
    }

    [Fact]
    public void A_failed_category_carries_the_reason()
    {
        CategoryResult<IssueSummary>.Failed("403 rate limited").Unavailable.Should().Be("403 rate limited");
    }

    [Fact]
    public void A_successful_category_has_no_reason()
    {
        CategoryResult<IssueSummary>.Ok([], truncated: false).Unavailable.Should().BeNull();
    }

    [Fact]
    public void Truncation_is_recorded_separately_from_emptiness()
    {
        var truncated = CategoryResult<CommitSummary>.Ok(
            [new CommitSummary("abc123", "a change", "someone", new DateTime(2026, 9, 19, 6, 0, 0, DateTimeKind.Utc))],
            truncated: true);

        truncated.Truncated.Should().BeTrue();
        truncated.Checked.Should().BeTrue();
    }
}
