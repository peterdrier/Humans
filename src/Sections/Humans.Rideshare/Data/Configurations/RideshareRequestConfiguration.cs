using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Rideshare.Data.Configurations;

internal sealed class RideshareRequestConfiguration : IEntityTypeConfiguration<RideshareRequest>
{
    public void Configure(EntityTypeBuilder<RideshareRequest> builder)
    {
        builder.ToTable("rideshare_requests");
        builder.HasKey(r => r.Id);

        // UserId is a bare cross-section reference: index for the per-user reads, no FK.
        builder.Property(r => r.UserId).IsRequired();
        builder.Property(r => r.Year).IsRequired();
        builder.Property(r => r.Direction).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(r => r.PickupPlaceLabel).HasMaxLength(200).IsRequired();
        builder.Property(r => r.PickupLatitude).IsRequired();
        builder.Property(r => r.PickupLongitude).IsRequired();
        builder.Property(r => r.DesiredDate).IsRequired();
        builder.Property(r => r.PartySize).IsRequired();
        builder.Property(r => r.LuggageLoad).IsRequired().HasConversion<string>().HasMaxLength(50);
        // Bool defaulting to false: IsRequired only — HasDefaultValue(false) is the sentinel trap.
        builder.Property(r => r.CanContributeToFuel).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(1000);
        builder.Property(r => r.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasIndex(r => r.UserId);
        builder.HasIndex(r => r.Year);
    }
}
