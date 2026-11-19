using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class GovernanceBodyLinksConfiguration : IEntityTypeConfiguration<GovernanceBodyLinks>
{
    public void Configure(EntityTypeBuilder<GovernanceBodyLinks> builder)
    {
        builder.ToTable("governance_body_links");

        builder.HasKey(b => b.Body);
        builder.Property(b => b.Body).HasConversion<string>().HasMaxLength(50);

        builder.Property(b => b.GoogleGroupPrefix).HasMaxLength(64);
        builder.Property(b => b.UpdatedAt).IsRequired();

        builder.HasMany(b => b.Files)
            .WithOne()
            .HasForeignKey(f => f.Body)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GovernanceBodyFileConfiguration : IEntityTypeConfiguration<GovernanceBodyFile>
{
    public void Configure(EntityTypeBuilder<GovernanceBodyFile> builder)
    {
        builder.ToTable("governance_body_files");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.Property(f => f.Body).HasConversion<string>().HasMaxLength(50);
        builder.Property(f => f.GoogleId).IsRequired().HasMaxLength(256);
        builder.Property(f => f.ResourceType).HasConversion<string>().HasMaxLength(50);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(512);
        builder.Property(f => f.Url).HasMaxLength(2048);
        builder.Property(f => f.PermissionLevel).HasConversion<string>().HasMaxLength(50);

        builder.HasIndex(f => new { f.Body, f.GoogleId }).IsUnique();
    }
}
