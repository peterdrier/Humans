using Humans.Campaigns.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Campaigns.Data.Configurations;

internal sealed class CampaignGrantConfiguration : IEntityTypeConfiguration<CampaignGrant>
{
    public void Configure(EntityTypeBuilder<CampaignGrant> builder)
    {
        builder.ToTable("campaign_grants");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.LatestEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(g => g.RedeemedAt);

        // UserId is a bare cross-section Guid column — no FK constraint, no nav.
        // Explicit index: replaces the convention index that died with the FK.
        // CampaignRepository filters UserId alone (:160,173,327,350,380) and the
        // unique (CampaignId, UserId) cannot serve a UserId-only lookup.
        builder.HasIndex(g => g.UserId);
        builder.HasIndex(g => new { g.CampaignId, g.UserId }).IsUnique();
        builder.HasIndex(g => g.CampaignCodeId).IsUnique();

        builder.HasOne(g => g.Campaign)
            .WithMany(c => c.Grants)
            .HasForeignKey(g => g.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.Code)
            .WithOne(c => c.Grant)
            .HasForeignKey<CampaignGrant>(g => g.CampaignCodeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
