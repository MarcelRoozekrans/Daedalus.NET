using System.Reflection;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Unit.Domain.CodeAnalysis;

/// <summary>
///     Characterises the structural equality of the 3 value objects in
///     <c>Daedalus.Domain.CodeAnalysis</c> — <see cref="AnalysisOutcome"/>, <see cref="CodeLocation"/>,
///     <see cref="RepositoryReference"/> — against their CURRENT base, verified by reflection to be
///     <c>CSharpFunctionalExtensions.ValueObject</c> (unlike the domain entities, there is no local
///     shadowing type here; <c>typeof(CodeLocation).BaseType</c> resolves straight to
///     <c>CSharpFunctionalExtensions.ValueObject</c> in the assembly named <c>CSharpFunctionalExtensions</c>).
/// </summary>
/// <remarks>
///     <para>
///     Each class's <c>GetEqualityComponents()</c> yields NORMALISED values, not raw properties: the
///     three nullable-string components on <see cref="AnalysisOutcome"/> and the two nullable-string
///     components on <see cref="RepositoryReference"/> are coalesced to <see cref="string.Empty"/>
///     before being yielded, and the two nullable-int components on <see cref="CodeLocation"/> are
///     coalesced to <c>0</c>. The practical effect, confirmed empirically below rather than assumed: a
///     <c>null</c> component and its coalesced default compare EQUAL today. A faithful conversion to
///     <c>ZeroAlloc.ValueObjects</c> must reproduce this via computed equality-only members, not by
///     marking the raw nullable properties directly (which would compare <c>null</c> and <c>""</c> as
///     unequal and silently narrow equality relative to today).
///     </para>
///     <para>
///     <see cref="RepositoryReference.Platform"/> cannot be varied independently of
///     <see cref="RepositoryReference.Url"/> through the public API — <c>Platform</c> is always derived
///     from <c>Url</c> by <c>DetectPlatform</c> inside <c>Create</c>, and the property setter is private.
///     The "differs in Platform only" case therefore uses reflection to overwrite the private setter
///     after construction, simulating the one realistic way <c>Url</c> and <c>Platform</c> could end up
///     inconsistent in practice: an owned-type row materialised by EF Core from a database whose stored
///     <c>Platform</c> predates a later change to <c>DetectPlatform</c> (e.g. GitLab detection was added
///     after some rows were already written with <c>Platform = None</c>).
///     </para>
/// </remarks>
public sealed class ValueObjectEqualityCharacterisationTests
{
    // ------------------------------------------------------------------
    // AnalysisOutcome — components: PullRequestUrl, CommitShaFinal, ValidationResult, HasFailedValidation
    // ------------------------------------------------------------------

    /// <summary>Would break if: the generated Equals stops comparing all 4 components, or GetHashCode diverges from Equals.</summary>
    [Fact]
    public void AnalysisOutcome_with_identical_components_is_equal()
    {
        var a = AnalysisOutcome.Empty().WithCompletion("https://pr/1", "sha1").WithValidation("ok", false);
        var b = AnalysisOutcome.Empty().WithCompletion("https://pr/1", "sha1").WithValidation("ok", false);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>Would break if: PullRequestUrl's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void AnalysisOutcome_differing_in_PullRequestUrl_only_is_not_equal()
    {
        var a = AnalysisOutcome.Empty().WithCompletion("https://pr/1", "sha1");
        var b = AnalysisOutcome.Empty().WithCompletion("https://pr/2", "sha1");

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: CommitShaFinal's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void AnalysisOutcome_differing_in_CommitShaFinal_only_is_not_equal()
    {
        var a = AnalysisOutcome.Empty().WithCompletion("https://pr/1", "sha1");
        var b = AnalysisOutcome.Empty().WithCompletion("https://pr/1", "sha2");

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: ValidationResult's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void AnalysisOutcome_differing_in_ValidationResult_only_is_not_equal()
    {
        var a = AnalysisOutcome.Empty().WithValidation("ok", false);
        var b = AnalysisOutcome.Empty().WithValidation("failed", false);

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: HasFailedValidation's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void AnalysisOutcome_differing_in_HasFailedValidation_only_is_not_equal()
    {
        var a = AnalysisOutcome.Empty().WithValidation("ok", false);
        var b = AnalysisOutcome.Empty().WithValidation("ok", true);

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: Equals starts throwing or returning true for a null comparand.</summary>
#pragma warning disable CA1508 // The comparand is deliberately a literal null to pin the null-handling branch of Equals itself.
    [Fact]
    public void AnalysisOutcome_is_not_equal_to_null_or_a_different_type()
    {
        var a = AnalysisOutcome.Empty();

        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not an AnalysisOutcome"));
    }
#pragma warning restore CA1508

    /// <summary>
    ///     PINS THE NORMALISATION QUIRK — see class remarks. Would break if a conversion marks the raw
    ///     nullable properties directly instead of the null-coalescing computed members, which would make
    ///     <c>null</c> and <c>""</c> compare UNEQUAL and silently narrow equality relative to today.
    /// </summary>
    [Fact]
    public void AnalysisOutcome_treats_null_and_empty_string_components_as_equal()
    {
        var withNulls = AnalysisOutcome.Empty();
        var withEmpties = AnalysisOutcome.Empty().WithCompletion("", "").WithValidation("", false);

        Assert.Equal(withNulls, withEmpties);
        Assert.Equal(withNulls.GetHashCode(), withEmpties.GetHashCode());
    }

    // ------------------------------------------------------------------
    // CodeLocation — components: FilePath, StartLine, EndLine
    // ------------------------------------------------------------------

    /// <summary>Would break if: the generated Equals stops comparing all 3 components, or GetHashCode diverges from Equals.</summary>
    [Fact]
    public void CodeLocation_with_identical_components_is_equal()
    {
        var a = CodeLocation.Create("File.cs", 10, 20).Value;
        var b = CodeLocation.Create("File.cs", 10, 20).Value;

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>Would break if: FilePath's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void CodeLocation_differing_in_FilePath_only_is_not_equal()
    {
        var a = CodeLocation.Create("File.cs", 10, 20).Value;
        var b = CodeLocation.Create("Other.cs", 10, 20).Value;

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: StartLine's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void CodeLocation_differing_in_StartLine_only_is_not_equal()
    {
        var a = CodeLocation.Create("File.cs", 10, 20).Value;
        var b = CodeLocation.Create("File.cs", 11, 20).Value;

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: EndLine's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void CodeLocation_differing_in_EndLine_only_is_not_equal()
    {
        var a = CodeLocation.Create("File.cs", 10, 20).Value;
        var b = CodeLocation.Create("File.cs", 10, 21).Value;

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: Equals starts throwing or returning true for a null comparand.</summary>
#pragma warning disable CA1508 // The comparand is deliberately a literal null to pin the null-handling branch of Equals itself.
    [Fact]
    public void CodeLocation_is_not_equal_to_null_or_a_different_type()
    {
        var a = CodeLocation.Create("File.cs", 10, 20).Value;

        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not a CodeLocation"));
    }
#pragma warning restore CA1508

    /// <summary>
    ///     PINS THE NORMALISATION QUIRK — see class remarks. Would break if a conversion marks the raw
    ///     nullable <c>int?</c> properties directly instead of the null-coalescing computed members, which
    ///     would make <c>null</c> and <c>0</c> compare UNEQUAL and silently narrow equality relative to
    ///     today.
    /// </summary>
    [Fact]
    public void CodeLocation_treats_null_and_zero_line_numbers_as_equal()
    {
        var withNulls = CodeLocation.Create("File.cs").Value;
        var withZeros = CodeLocation.Create("File.cs", 0, 0).Value;

        Assert.Equal(withNulls, withZeros);
        Assert.Equal(withNulls.GetHashCode(), withZeros.GetHashCode());
    }

    // ------------------------------------------------------------------
    // RepositoryReference — components: Url, Branch, CommitSha, Platform
    // ------------------------------------------------------------------

    /// <summary>Would break if: the generated Equals stops comparing all 4 components, or GetHashCode diverges from Equals.</summary>
    [Fact]
    public void RepositoryReference_with_identical_components_is_equal()
    {
        var a = RepositoryReference.Create("https://github.com/org/repo", "main", "sha1").Value;
        var b = RepositoryReference.Create("https://github.com/org/repo", "main", "sha1").Value;

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>Would break if: Url's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void RepositoryReference_differing_in_Url_only_is_not_equal()
    {
        // Both URLs are on the same host so Platform (GitHub) is identical for both — isolates Url.
        var a = RepositoryReference.Create("https://github.com/org/repo-a", "main", "sha1").Value;
        var b = RepositoryReference.Create("https://github.com/org/repo-b", "main", "sha1").Value;

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: Branch's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void RepositoryReference_differing_in_Branch_only_is_not_equal()
    {
        var a = RepositoryReference.Create("https://github.com/org/repo", "main", "sha1").Value;
        var b = RepositoryReference.Create("https://github.com/org/repo", "develop", "sha1").Value;

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: CommitSha's [EqualityMember] is missing — the two would compare equal.</summary>
    [Fact]
    public void RepositoryReference_differing_in_CommitSha_only_is_not_equal()
    {
        var a = RepositoryReference.Create("https://github.com/org/repo", "main", "sha1").Value;
        var b = RepositoryReference.Create("https://github.com/org/repo", "main", "sha2").Value;

        Assert.NotEqual(a, b);
    }

    /// <summary>
    ///     Would break if: Platform's [EqualityMember] is missing — the two would compare equal. Platform
    ///     cannot be varied independently through the public API (see class remarks), so this uses
    ///     reflection to overwrite the private setter after construction — the two instances share an
    ///     identical <c>Url</c> (and therefore would share an identical <c>DetectPlatform</c> result) and
    ///     differ ONLY in the stored <c>Platform</c> value.
    /// </summary>
    [Fact]
    public void RepositoryReference_differing_in_Platform_only_is_not_equal()
    {
        var a = RepositoryReference.Create("https://github.com/org/repo", "main", "sha1").Value;
        var b = RepositoryReference.Create("https://github.com/org/repo", "main", "sha1").Value;
        Assert.Equal(RepositoryPlatform.GitHub, a.Platform);
        Assert.Equal(RepositoryPlatform.GitHub, b.Platform);

        var platformProperty = typeof(RepositoryReference).GetProperty(nameof(RepositoryReference.Platform))!;
        platformProperty.SetValue(b, RepositoryPlatform.GitLab);

        Assert.NotEqual(a, b);
    }

    /// <summary>Would break if: Equals starts throwing or returning true for a null comparand.</summary>
#pragma warning disable CA1508 // The comparand is deliberately a literal null to pin the null-handling branch of Equals itself.
    [Fact]
    public void RepositoryReference_is_not_equal_to_null_or_a_different_type()
    {
        var a = RepositoryReference.Create("https://github.com/org/repo").Value;

        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not a RepositoryReference"));
    }
#pragma warning restore CA1508

    /// <summary>
    ///     PINS THE NORMALISATION QUIRK — see class remarks. Would break if a conversion marks the raw
    ///     nullable <c>Branch</c>/<c>CommitSha</c> properties directly instead of the null-coalescing
    ///     computed members, which would make <c>null</c> and <c>""</c> compare UNEQUAL and silently
    ///     narrow equality relative to today.
    /// </summary>
    [Fact]
    public void RepositoryReference_treats_null_and_empty_string_components_as_equal()
    {
        var withNulls = RepositoryReference.Create("https://github.com/org/repo").Value;
        var withEmpties = RepositoryReference.Create("https://github.com/org/repo", "", "").Value;

        Assert.Equal(withNulls, withEmpties);
        Assert.Equal(withNulls.GetHashCode(), withEmpties.GetHashCode());
    }
}
