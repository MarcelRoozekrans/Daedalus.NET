namespace Daedalus.Web.Services;

/// <summary>Who started a run, as the run header and the Runs list name them: the one place the fallback is written.</summary>
public static class RunStarter
{
    /// <summary>The name shown for a starter that records no display name. A subject id is never shown instead.</summary>
    public const string Unknown = "unknown";

    /// <summary>The starter's display name, or <see cref="Unknown"/> when the run records none.</summary>
    public static string Name(string? displayName) => displayName ?? Unknown;
}
