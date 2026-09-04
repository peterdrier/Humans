using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Rideshare.Data.Configurations;

internal sealed class RideshareSettingsConfiguration : IEntityTypeConfiguration<RideshareSettings>
{
    public void Configure(EntityTypeBuilder<RideshareSettings> builder)
    {
        builder.ToTable("rideshare_settings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Year).IsRequired();
        builder.Property(s => s.DestinationLabel).HasMaxLength(200).IsRequired();
        builder.Property(s => s.DestinationLatitude).IsRequired();
        builder.Property(s => s.DestinationLongitude).IsRequired();
        builder.Property(s => s.InboundWindowStart).IsRequired();
        builder.Property(s => s.InboundWindowEnd).IsRequired();
        builder.Property(s => s.OutboundWindowStart).IsRequired();
        builder.Property(s => s.OutboundWindowEnd).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // One row per burn year. No HasData seed: the row is created on first admin save.
        builder.HasIndex(s => s.Year).IsUnique();
    }
}
