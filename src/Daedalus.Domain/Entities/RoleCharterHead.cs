namespace Daedalus.Domain.Entities;

/// <summary>
///     Which <see cref="RoleCharterVersion"/> is a role's current one, and whether the role is active at all
///     (table <c>RoleCharters</c>). A thin pointer, not a duplicate of the content — <c>PostgresRoleCharterStore</c>
///     joins this to <see cref="RoleCharterVersion"/> by (<see cref="Role"/>, <see cref="CurrentHash"/>) to compute
///     the Thalos <c>RoleCharter.IsActive</c> flag for every listed version, and flips <see cref="IsActive"/> here —
///     never a row in the versions table — when a charter disappears from every configured root.
/// </summary>
public sealed class RoleCharterHead
{
    /// <summary>Gets the role this points at (primary key).</summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>Gets the content hash of the role's current <see cref="RoleCharterVersion"/>.</summary>
    public string CurrentHash { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the role is still synced from a configured root. False once every root stops naming it.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets when this pointer was last written by a sync (UTC).</summary>
    public DateTime UpdatedAt { get; private set; }

    private RoleCharterHead() { } // EF Core

    /// <summary>Creates the first head row for a role, active by construction — an upsert always makes its charter the current, active version.</summary>
    public static RoleCharterHead Create(string role, string currentHash, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentHash);

        return new RoleCharterHead
        {
            Role = role,
            CurrentHash = currentHash,
            IsActive = true,
            UpdatedAt = updatedAt,
        };
    }

    /// <summary>Repoints this role at a newly upserted version, reactivating it if a prior sweep had deactivated it.</summary>
    public void SetCurrent(string currentHash, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentHash);

        CurrentHash = currentHash;
        IsActive = true;
        UpdatedAt = updatedAt;
    }

    /// <summary>Marks the role inactive — its versions stay in the versions table, only this pointer's activity flips.</summary>
    public void Deactivate(DateTime updatedAt)
    {
        IsActive = false;
        UpdatedAt = updatedAt;
    }
}
