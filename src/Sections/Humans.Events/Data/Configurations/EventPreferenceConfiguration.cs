using Humans.Events.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Events.Data.Configurations;

internal sealed class EventPreferenceConfiguration : IEntityTypeConfiguration<EventPreference>
{
    public void Configure(EntityTypeBuilder<EventPreference> builder)
    {
        builder.ToTable("event_preferences");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ExcludedCategorySlugs).HasMaxLength(1000).IsRequired();

        builder.HasIndex(p => p.UserId).IsUnique();
    }
}
