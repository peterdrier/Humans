using Humans.Tickets.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Tickets.Data.Configurations;

internal sealed class TicketSettingsConfiguration : IEntityTypeConfiguration<TicketSettings>
{
    public void Configure(EntityTypeBuilder<TicketSettings> builder)
    {
        builder.ToTable("ticket_settings");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.IsScheduledSyncEnabled).IsRequired();

        builder.HasData(new TicketSettings { Id = 1, IsScheduledSyncEnabled = false });
    }
}
