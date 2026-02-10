using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class VolunteerHistoryEntryConfiguration : IEntityTypeConfiguration<VolunteerHistoryEntry>
{
    public void Configure(EntityTypeBuilder<VolunteerHistoryEntry> builder)
    {
        builder.ToTable("volunteer_history_entries");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Date)
            .IsRequired();

        builder.Property(v => v.EventName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(v => v.Description)
            .HasMaxLength(2000);

        builder.Property(v => v.CreatedAt)
            .IsRequired();

        builder.Property(v => v.UpdatedAt)
            .IsRequired();

        builder.HasOne(v => v.Profile)
            .WithMany(p => p.VolunteerHistory)
            .HasForeignKey(v => v.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(v => v.ProfileId);
    }
}
