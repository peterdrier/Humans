using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Rideshare.Data.Configurations;

internal sealed class RideshareTripConfiguration : IEntityTypeConfiguration<RideshareTrip>
{
    public void Configure(EntityTypeBuilder<RideshareTrip> builder)
    {
        builder.ToTable("rideshare_trips");
        builder.HasKey(t => t.Id);

        // UserId is a bare cross-section reference: index for the per-user reads, no FK.
        builder.Property(t => t.UserId).IsRequired();
        builder.Property(t => t.Year).IsRequired();
        builder.Property(t => t.Direction).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.MemberPlaceLabel).HasMaxLength(200).IsRequired();
        builder.Property(t => t.MemberLatitude).IsRequired();
        builder.Property(t => t.MemberLongitude).IsRequired();
        builder.Property(t => t.WaypointsJson).HasColumnType("text");
        builder.Property(t => t.RouteGeoJson).HasColumnType("text");
        builder.Property(t => t.DepartureDate).IsRequired();
        builder.Property(t => t.ExpectedDurationDays).IsRequired();
        builder.Property(t => t.OvernightPlan).HasMaxLength(1000);
        builder.Property(t => t.VehicleType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.SeatsOffered).IsRequired();
        builder.Property(t => t.LuggageCapacity).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.CapacityNote).HasMaxLength(500);
        builder.Property(t => t.Restrictions).HasMaxLength(500);
        builder.Property(t => t.CostNote).HasMaxLength(500);
        // Bool defaulting to false: IsRequired only — HasDefaultValue(false) is the sentinel trap.
        builder.Property(t => t.WillingToDetour).IsRequired();
        builder.Property(t => t.CostSharing).IsRequired().HasConversion<string>().HasMaxLength(50);
        // LinkedTripId is a display-only soft link to the paired leg: no FK, no index.
        builder.Property(t => t.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        builder.HasIndex(t => t.UserId);
        builder.HasIndex(t => t.Year);
    }
}
