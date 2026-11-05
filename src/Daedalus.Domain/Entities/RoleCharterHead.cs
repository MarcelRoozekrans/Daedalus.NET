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
    public string Role { get; }

    /// <summary>Gets the content hash of the role's current <see cref="RoleCharterVersion"/>.</summary>
    public string CurrentHash { get; private set; }

    /// <summary>Gets a value indicating whether the role is still synced from a configured root. False once every root stops naming it.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets when this pointer was last written by a sync (UTC).</summary>
    public DateTime UpdatedAt { get; private set; }

    private RoleCharterHead(string role, string currentHash, bool isActive, DateTime updatedAt)
    {
        Role = role;
        CurrentHash = currentHash;
        IsActive = isActive;
        UpdatedAt = updatedAt;
    }

    /// <summary>Creates the first head row for a role, active by construction — an upsert always makes its charter the current, active version.</summary>
    public static RoleCharterHead Create(string role, string currentHash, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentHash);

        return new RoleCharterHead(role, currentHash, isActive: true, updatedAt);
    }

    /// <summary>
    ///     Rebuilds a stored head from its columns. For persistence only: it sets every value as given and
    ///     validates nothing.
    /// </summary>
    /// <param name="role">The role the pointer is for.</param>
    /// <param name="currentHash">The content hash of the role's current version.</param>
    /// <param name="isActive">Whether the role is still synced from a configured root.</param>
    /// <param name="updatedAt">When the pointer was last written.</param>
    /// <returns>The head the row holds.</returns>
    public static RoleCharterHead Rehydrate(string role, string currentHash, bool isActive, DateTime updatedAt) =>
        new(role, currentHash, isActive, updatedAt);

    /// <summary>Repoints this role at a newly upserted version, reactivating it if a prior sweep had deactivated it.</summary>
    public void SetCurrent(string currentHash, DateTime updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentHash);

        CurrentHash = currentHash;
        IsActive = true;
        UpdatedAt = updatedAt;
    }
}
