using Daedalus.Agents.GitHub;

namespace Daedalus.Tests.Unit.Agents.GitHub;

public class RepoRefTests
{
    [Theory]
    [InlineData("owner/repo", "owner", "repo")]
    [InlineData("Marcel-R/Daedalus.NET", "Marcel-R", "Daedalus.NET")]
    public void Parse_accepts_a_well_formed_reference(string input, string owner, string name)
    {
        var result = RepoRef.Parse(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Owner.Should().Be(owner);
        result.Value.Name.Should().Be(name);
        result.Value.ToString().Should().Be(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noslash")]
    [InlineData("too/many/parts")]
    [InlineData("/repo")]
    [InlineData("owner/")]
    [InlineData("../etc")]
    [InlineData("owner/repo?query=1")]
    public void Parse_rejects_anything_that_would_build_a_malformed_url(string input)
    {
        RepoRef.Parse(input).IsFailure.Should().BeTrue(
            "an agent passes this string straight from a model, and a bad value must not reach HttpClient");
    }
}
