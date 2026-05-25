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

        // Dormant vendor-writeback columns — no longer used by any code; kept mapped
        // so the columns are not dropped in this PR. A follow-up PR drops them after
        // prod soak (memory/architecture/no-drops-until-prod-verified.md).
        builder.Property(x => x.VendorResult)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.VendorMessage).HasMaxLength(2000);
        builder.Property(x => x.NewVendorTicketId).HasMaxLength(64);

        builder.Property(x => x.VendorStepsJson)
            .IsRequired()
            .HasDefaultValue("[]")
            .HasColumnType("text");

        builder.Property(x => x.AdminNotes).HasMaxLength(1000);

        builder.Property(x => x.RequestedAt).IsRequired();

        // Indexes for the homepage card lookup and the admin queue.
        builder.HasIndex(x => new { x.SenderUserId, x.Status });
        builder.HasIndex(x => x.Status);
    }
}
