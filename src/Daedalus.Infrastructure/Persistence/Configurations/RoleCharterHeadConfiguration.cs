using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the <see cref="RoleCharterHead"/> aggregate (table <c>RoleCharters</c>) — the
///     thin "which version is current, and is the role active" pointer beside the full history in
///     <c>RoleCharterVersions</c>.
/// </summary>
internal sealed class RoleCharterHeadConfiguration : IEntityTypeConfiguration<RoleCharterHead>
{
    public void Configure(EntityTypeBuilder<RoleCharterHead> builder)
    {
        builder.ToTable("RoleCharters");

        // The role is the primary key: one pointer row per role, matching Thalos.NET's one-current-version-per-role rule.
        builder.HasKey(h => h.Role);

        builder.Property(h => h.Role)
            .IsRequired()
            .HasMaxLength(RoleCharterVersion.MaxRoleLength);

        builder.Property(h => h.CurrentHash)
            .IsRequired()
            .HasMaxLength(RoleCharterVersion.MaxContentHashLength);

        builder.Property(h => h.IsActive)
            .IsRequired();

        builder.Property(h => h.UpdatedAt)
            .IsRequired();

        // Every list/sync operation filters or sweeps on IsActive; the table is role-sized, so this is the only index it needs.
        builder.HasIndex(h => h.IsActive)
            .HasDatabaseName("IX_RoleCharterHead_IsActive");
    }
}
