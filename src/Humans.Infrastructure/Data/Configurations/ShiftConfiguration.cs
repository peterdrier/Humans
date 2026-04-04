using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace Humans.Infrastructure.Data.Configurations;

public class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.ToTable("shifts");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.Property(e => e.IsAllDay);

        builder.Property(s => s.Duration)
            .HasConversion(
                d => (long)d.TotalSeconds,
                s => Duration.FromSeconds(s));

        builder.HasIndex(s => s.RotaId);

        builder.HasOne(s => s.Rota)
            .WithMany(r => r.Shifts)
            .HasForeignKey(s => s.RotaId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
