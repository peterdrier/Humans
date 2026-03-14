using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampaignCodeConfiguration : IEntityTypeConfiguration<CampaignCode>
{
    public void Configure(EntityTypeBuilder<CampaignCode> builder)
    {
        builder.ToTable("campaign_codes");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Code).HasMaxLength(200).IsRequired();

        builder.HasIndex(c => new { c.CampaignId, c.Code }).IsUnique();

        builder.HasOne(c => c.Campaign)
            .WithMany(c => c.Codes)
            .HasForeignKey(c => c.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
