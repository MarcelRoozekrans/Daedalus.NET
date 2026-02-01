using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daedalus.Infrastructure.Persistence.Configurations;

/// <summary>
///     EF Core configuration for the RepositoryConfiguration aggregate root.
/// </summary>
internal sealed class RepositoryConfigurationEntityConfiguration : IEntityTypeConfiguration<RepositoryConfiguration>
{
    public void Configure(EntityTypeBuilder<RepositoryConfiguration> entity)
    {
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(256);

        entity.Property(e => e.Url)
            .IsRequired()
            .HasMaxLength(1024);

        entity.Property(e => e.Platform)
            .IsRequired()
            .HasMaxLength(50);

        entity.Property(e => e.DefaultBranch)
            .IsRequired()
            .HasMaxLength(256);

        entity.Property(e => e.AuthenticationMethod)
            .HasMaxLength(100);

        entity.Property(e => e.CredentialIdentifier)
            .HasMaxLength(512);

        entity.Property(e => e.Description)
            .HasMaxLength(2000);

        // Configure concurrency token for optimistic locking
        entity.Property(e => e.RowVersion)
            .IsRowVersion();

        entity.HasIndex(e => e.Name)
            .HasDatabaseName("IX_RepositoryConfiguration_Name");

        entity.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_RepositoryConfiguration_IsActive");

        entity.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_RepositoryConfiguration_CreatedAt");
    }
}
