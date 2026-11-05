using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Shifts.Data.Configurations;

internal sealed class EventSettingsConfiguration : IEntityTypeConfiguration<EventSettings>
{
    public void Configure(EntityTypeBuilder<EventSettings> builder)
    {
        builder.ToTable("event_settings");
        builder.HasKey(e => e.Id);
    }
}
