using Humans.Grants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Grants.Data.Configurations;

internal sealed class GrantProgramConfiguration : IEntityTypeConfiguration<GrantProgram>
{
    public void Configure(EntityTypeBuilder<GrantProgram> b)
    {
        b.ToTable("grant_programs");
        b.HasKey(p => p.Id);

        b.Property(p => p.Name).IsRequired().HasMaxLength(200);
        b.Property(p => p.Entitles).IsRequired().HasMaxLength(500);
        b.Property(p => p.Description).HasMaxLength(2000);
        b.Property(p => p.Year).IsRequired();
        b.HasIndex(p => p.EventSettingsId);
        b.Property(p => p.IssuedBy).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.SourceKey).HasMaxLength(32);
        b.Property(p => p.Status).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.CreatedByUserId).IsRequired();
        b.Property(p => p.CreatedAt).IsRequired();
        b.Property(p => p.UpdatedAt).IsRequired();

        b.HasMany(p => p.Managers)
            .WithOne(m => m.Program)
            .HasForeignKey(m => m.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(p => p.Grants)
            .WithOne(g => g.Program)
            .HasForeignKey(g => g.ProgramId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GrantProgramManagerConfiguration : IEntityTypeConfiguration<GrantProgramManager>
{
    public void Configure(EntityTypeBuilder<GrantProgramManager> b)
    {
        b.ToTable("grant_program_managers");
        b.HasKey(m => new { m.ProgramId, m.UserId });

        b.Property(m => m.UserId).IsRequired();
        b.Property(m => m.AddedByUserId).IsRequired();
        b.Property(m => m.AddedAt).IsRequired();

        b.HasIndex(m => m.UserId);
    }
}

internal sealed class GrantConfiguration : IEntityTypeConfiguration<Grant>
{
    public void Configure(EntityTypeBuilder<Grant> b)
    {
        b.ToTable("grants");
        b.HasKey(g => g.Id);

        b.Property(g => g.ProgramId).IsRequired();
        b.Property(g => g.UserId).IsRequired();
        b.Property(g => g.SourceRef).HasMaxLength(128);
        b.Property(g => g.IssuedAt).IsRequired();
        b.Property(g => g.IssuedByUserId).IsRequired();
        b.Property(g => g.RevokeReason).HasMaxLength(500);

        b.HasIndex(g => g.UserId);
        // Revoked rows stay as the record, so the uniqueness is over live grants only.
        b.HasIndex(g => new { g.ProgramId, g.UserId })
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL");
    }
}
