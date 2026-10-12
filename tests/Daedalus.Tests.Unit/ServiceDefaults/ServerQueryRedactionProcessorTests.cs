using Daedalus.ServiceDefaults;

namespace Daedalus.Tests.Unit.ServiceDefaults;

/// <summary><see cref="ServerQueryRedactionProcessor"/> redacts an access token in a server span's query and nothing else.</summary>
public sealed class ServerQueryRedactionProcessorTests
{
    /// <summary>
    ///     The token's value becomes Redacted wherever it sits, any case, with or without a leading '?', and other
    ///     parameters keep their values.
    ///     Red: compare the name case-sensitively. Red: drop the parameters after the token.
    /// </summary>
    [Theory]
    [InlineData("access_token=zqx-t", "access_token=Redacted")]
    [InlineData("?id=1&Access_Token=zqx-t&x=2", "?id=1&Access_Token=Redacted&x=2")]
    [InlineData("id=1&access_token=", "id=1&access_token=Redacted")]
    public void The_token_is_redacted(string query, string expected) =>
        ServerQueryRedactionProcessor.Redact(query).Should().Be(expected);

    /// <summary>
    ///     A query with no token is left alone, signalled by null.
    ///     Red: redact every parameter.
    /// </summary>
    [Theory]
    [InlineData("status=Awaiting&take=25")]
    [InlineData("my_access_token=1")]
    public void A_query_without_a_token_is_untouched(string query) =>
        ServerQueryRedactionProcessor.Redact(query).Should().BeNull();
}
