using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class GuideEventConfiguration : IEntityTypeConfiguration<GuideEvent>
{
    public void Configure(EntityTypeBuilder<GuideEvent> builder)
    {
        builder.ToTable("guide_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Title).HasMaxLength(80).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(300).IsRequired();
        builder.Property(e => e.LocationNote).HasMaxLength(120);
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

        builder.HasOne(e => e.Camp)
            .WithMany()
            .HasForeignKey(e => e.CampId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.GuideSharedVenue)
            .WithMany(v => v.GuideEvents)
            .HasForeignKey(e => e.GuideSharedVenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.SubmitterUser)
            .WithMany()
            .HasForeignKey(e => e.SubmitterUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Category)
            .WithMany(c => c.GuideEvents)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
