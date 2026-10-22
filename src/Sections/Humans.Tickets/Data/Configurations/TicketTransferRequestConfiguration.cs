using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Data.Configurations;

internal sealed class TicketTransferRequestConfiguration : IEntityTypeConfiguration<TicketTransferRequest>
{
    public void Configure(EntityTypeBuilder<TicketTransferRequest> builder)
    {
        builder.ToTable("ticket_transfer_requests");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OriginalTicketAttendeeId)
            .IsRequired();

        builder.HasOne(x => x.OriginalTicketAttendee)
            .WithMany()
            .HasForeignKey(x => x.OriginalTicketAttendeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.SenderUserId).IsRequired();
        builder.Property(x => x.ReceiverUserId).IsRequired();

        builder.Property(x => x.ReceiverLegalName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.ReceiverEmail)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(x => x.SenderReason)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // VendorStepsJson is dormant: unread, dropped after prod soak.
        builder.Property(x => x.VendorResult)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.VendorMessage).HasMaxLength(2000);
        builder.Property(x => x.NewVendorTicketId).HasMaxLength(64);
        builder.Property(x => x.VendorHoldId).HasMaxLength(64);

        builder.Property(x => x.VendorStepsJson)
            .IsRequired()
            .HasDefaultValue("[]")
            .HasColumnType("text");

        builder.Property(x => x.AdminNotes).HasMaxLength(1000);

        builder.Property(x => x.RequestedAt).IsRequired();

        builder.HasIndex(x => new { x.SenderUserId, x.Status });
        builder.HasIndex(x => x.Status);
    }
}
