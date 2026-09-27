using Thalos.Workflow;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     The principal a test starts a run as when the test is not about who started it. Thalos requires a starter
///     on every run, so a test that builds a start request by hand passes this one rather than inventing its own.
/// </summary>
internal static class TestPrincipals
{
    /// <summary>The starter of every run a test starts directly against the store or the run starter.</summary>
    public static readonly RunPrincipal Starter = new("test-starter", ["admin"]);
}
