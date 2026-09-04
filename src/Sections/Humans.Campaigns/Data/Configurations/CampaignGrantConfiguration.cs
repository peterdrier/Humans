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
        // Explicit index: the repository's grant-by-user reads, GDPR export/erase
        // and the merge fold all filter UserId alone, which the unique
        // (CampaignId, UserId) index cannot serve.
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
