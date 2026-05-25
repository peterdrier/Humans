using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Title).HasMaxLength(80).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(450).IsRequired();
        builder.Property(e => e.LocationNote).HasMaxLength(120);
        builder.Property(e => e.Host).HasMaxLength(40);
        builder.Property(e => e.RecurrenceDays).HasMaxLength(100);
        builder.Property(e => e.AdminNotes).HasMaxLength(1000);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.CampId);
        builder.HasIndex(e => e.GuideSharedVenueId);
        builder.HasIndex(e => e.SubmitterUserId);

        builder.HasOne(e => e.EventVenue)
            .WithMany(v => v.Events)
            .HasForeignKey(e => e.GuideSharedVenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Category)
            .WithMany(c => c.Events)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
