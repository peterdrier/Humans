using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Camps.Data.Configurations;

internal sealed class CampEventSettingsConfiguration : IEntityTypeConfiguration<CampEventSettings>
{
    public void Configure(EntityTypeBuilder<CampEventSettings> builder)
    {
        builder.ToTable("camp_event_settings");
        builder.HasKey(s => s.EventSettingsId);
        builder.Property(s => s.EventSettingsId).ValueGeneratedNever();
    }
}
