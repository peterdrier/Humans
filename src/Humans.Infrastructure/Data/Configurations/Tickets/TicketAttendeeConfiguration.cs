using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Tickets;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
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

        builder.HasOne(a => a.MatchedUser)
            .WithMany()
            .HasForeignKey(a => a.MatchedUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => a.AttendeeEmail);
        builder.HasIndex(a => a.MatchedUserId);
        builder.HasIndex(a => a.TicketOrderId);
    }
}
