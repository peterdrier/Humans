using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Tickets;

public class TicketAttendeeConfiguration : IEntityTypeConfiguration<TicketAttendee>
{
    public void Configure(EntityTypeBuilder<TicketAttendee> builder)
    {
        builder.ToTable("ticket_attendees");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.VendorTicketId)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(a => a.VendorTicketId)
            .IsUnique();

        builder.Property(a => a.AttendeeName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.AttendeeEmail)
            .HasMaxLength(320);

        builder.Property(a => a.TicketTypeName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.Price)
            .HasPrecision(10, 2);

        builder.Property(a => a.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(a => a.VendorEventId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.SyncedAt)
            .IsRequired();

        // EF needs the nav ref to configure the cross-section FK + cascade.
        // The nav is [Obsolete] for the Application layer (design-rules §6c).
#pragma warning disable CS0618
        builder.HasOne(a => a.MatchedUser)
            .WithMany()
            .HasForeignKey(a => a.MatchedUserId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        builder.HasIndex(a => a.AttendeeEmail);
        builder.HasIndex(a => a.MatchedUserId);
        builder.HasIndex(a => a.TicketOrderId);
    }
}
