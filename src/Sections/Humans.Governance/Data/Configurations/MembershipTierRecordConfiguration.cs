using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Governance.Data.Configurations;

internal sealed class MembershipTierRecordConfiguration : IEntityTypeConfiguration<MembershipTierRecord>
{
    public void Configure(EntityTypeBuilder<MembershipTierRecord> builder)
    {
        builder.ToTable("governance_membership_tiers");

        builder.HasKey(t => t.UserId);
        builder.Property(t => t.UserId).ValueGeneratedNever();

        // Stored by name, like applications.MembershipTier and the profiles column it replaces.
        builder.Property(t => t.Tier)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(t => t.GrantedAt)
            .IsRequired();

        // GrantedByApplicationId is a bare Guid: the row outlives nothing it points at
        // (applications are never deleted), and a bare id keeps the copy free of ordering.
    }
}
