using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations.Tickets;

public class TicketSyncStateConfiguration : IEntityTypeConfiguration<TicketSyncState>
{
    public void Configure(EntityTypeBuilder<TicketSyncState> builder)
    {
        builder.ToTable("ticket_sync_state");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.VendorEventId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(s => s.SyncStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(s => s.LastError)
            .HasMaxLength(2000);

        // Seed the singleton row
        builder.HasData(new
        {
            Id = 1,
            VendorEventId = string.Empty,
            SyncStatus = TicketSyncStatus.Idle,
        });
    }
}
