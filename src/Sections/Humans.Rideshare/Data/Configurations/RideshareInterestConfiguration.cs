using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Rideshare.Data.Configurations;

internal sealed class RideshareInterestConfiguration : IEntityTypeConfiguration<RideshareInterest>
{
    public void Configure(EntityTypeBuilder<RideshareInterest> builder)
    {
        builder.ToTable("rideshare_interests");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.FromUserId).IsRequired();
        builder.Property(i => i.TripId).IsRequired();
        builder.Property(i => i.Seats).IsRequired();
        builder.Property(i => i.Message).HasMaxLength(1000);
        builder.Property(i => i.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasIndex(i => i.FromUserId);

        builder.HasOne(i => i.Trip)
            .WithMany(t => t.Interests)
            .HasForeignKey(i => i.TripId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Request)
            .WithMany()
            .HasForeignKey(i => i.RequestId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
