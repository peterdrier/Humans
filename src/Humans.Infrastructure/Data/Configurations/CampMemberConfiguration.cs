using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampMemberConfiguration : IEntityTypeConfiguration<CampMember>
{
    public void Configure(EntityTypeBuilder<CampMember> builder)
    {
        builder.ToTable("camp_members");

        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(m => m.RequestedAt).IsRequired();

        builder.HasOne(m => m.CampSeason)
            .WithMany()
            .HasForeignKey(m => m.CampSeasonId)
            .OnDelete(DeleteBehavior.Cascade);

        // Partial unique index: one active-or-pending membership per (season, user).
        // Removed rows are allowed to coexist so users can re-request later.
        builder.HasIndex(m => new { m.CampSeasonId, m.UserId })
            .HasFilter("\"Status\" <> 'Removed'")
            .IsUnique()
            .HasDatabaseName("IX_camp_members_active_unique");

        builder.HasIndex(m => m.UserId);
    }
}
