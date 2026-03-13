using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class BarrioConfiguration : IEntityTypeConfiguration<Barrio>
{
    public void Configure(EntityTypeBuilder<Barrio> builder)
    {
        builder.ToTable("barrios");

        builder.Property(b => b.Slug).HasMaxLength(256).IsRequired();
        builder.Property(b => b.ContactEmail).HasMaxLength(256).IsRequired();
        builder.Property(b => b.ContactPhone).HasMaxLength(64).IsRequired();
        builder.Property(b => b.WebOrSocialUrl).HasMaxLength(512);
        builder.Property(b => b.ContactMethod).HasMaxLength(512).IsRequired();

        builder.HasIndex(b => b.Slug).IsUnique();

        builder.HasOne(b => b.CreatedByUser)
            .WithMany()
            .HasForeignKey(b => b.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(b => b.Seasons)
            .WithOne(s => s.Barrio)
            .HasForeignKey(s => s.BarrioId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.Leads)
            .WithOne(l => l.Barrio)
            .HasForeignKey(l => l.BarrioId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.HistoricalNames)
            .WithOne(h => h.Barrio)
            .HasForeignKey(h => h.BarrioId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.Images)
            .WithOne(i => i.Barrio)
            .HasForeignKey(i => i.BarrioId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
