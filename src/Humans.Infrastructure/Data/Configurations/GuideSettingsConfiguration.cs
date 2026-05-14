using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class GuideSettingsConfiguration : IEntityTypeConfiguration<EventGuideSettings>
{
    public void Configure(EntityTypeBuilder<EventGuideSettings> builder)
    {
        builder.ToTable("guide_settings");
        builder.HasKey(g => g.Id);

        builder.HasIndex(g => g.EventSettingsId).IsUnique();

        builder.HasOne(g => g.EventSettings)
            .WithOne()
            .HasForeignKey<EventGuideSettings>(g => g.EventSettingsId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
