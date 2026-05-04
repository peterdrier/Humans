using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Tickets;

public sealed class TicketTransferRequestConfiguration : IEntityTypeConfiguration<TicketTransferRequest>
{
    public void Configure(EntityTypeBuilder<TicketTransferRequest> builder)
    {
        builder.ToTable("ticket_transfer_requests");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OriginalTicketAttendeeId)
            .IsRequired();

        builder.HasOne(x => x.OriginalTicketAttendee)
            .WithMany() // no inverse collection on TicketAttendee — keep that aggregate clean
            .HasForeignKey(x => x.OriginalTicketAttendeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.RequesterUserId).IsRequired();
        builder.Property(x => x.RecipientUserId).IsRequired();

        builder.Property(x => x.RecipientDisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.RecipientEmail)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(x => x.RequesterReason)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.VendorResult)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.VendorMessage).HasMaxLength(2000);
        builder.Property(x => x.NewVendorTicketId).HasMaxLength(64);
        builder.Property(x => x.AdminNotes).HasMaxLength(1000);

        builder.Property(x => x.RequestedAt).IsRequired();

        // Indexes:
        // - one Pending row per original attendee (enforces "only one Pending request per ticket")
        builder.HasIndex(x => x.OriginalTicketAttendeeId)
            .IsUnique()
            .HasFilter("status = 'Pending'");

        // - by requester (homepage card)
        builder.HasIndex(x => new { x.RequesterUserId, x.Status });

        // - by status (admin queue)
        builder.HasIndex(x => x.Status);
    }
}
