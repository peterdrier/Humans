using Humans.Events.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Events.Data.Configurations;

internal sealed class EventGuideSettingsConfiguration : IEntityTypeConfiguration<EventGuideSettings>
{
    public void Configure(EntityTypeBuilder<EventGuideSettings> builder)
    {
        builder.ToTable("event_guide_settings");
        builder.HasKey(g => g.Id);

        builder.HasIndex(g => g.EventSettingsId).IsUnique();
    }
}
