using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class PleoBookingHoldConfiguration : IEntityTypeConfiguration<PleoBookingHold>
{
    public void Configure(EntityTypeBuilder<PleoBookingHold> b)
    {
        b.ToTable("pleo_booking_holds");
        b.HasKey(x => x.PleoEntryId);
        b.Property(x => x.PleoEntryId).HasMaxLength(64).ValueGeneratedNever();
    }
}
