using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampaignGrantConfiguration : IEntityTypeConfiguration<CampaignGrant>
{
    public void Configure(EntityTypeBuilder<CampaignGrant> builder)
    {
        builder.ToTable("campaign_grants");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.LatestEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(20);

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

        builder.HasOne(g => g.User)
            .WithMany()
            .HasForeignKey(g => g.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
