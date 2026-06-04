using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class ShiftObligationConfiguration : IEntityTypeConfiguration<ShiftObligation>
{
    public void Configure(EntityTypeBuilder<ShiftObligation> builder)
    {
        builder.ToTable("shift_obligations");

        builder.Property(o => o.TargetType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(o => o.Applicability).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(o => o.CampRoleSlug).HasMaxLength(64).IsRequired();

        builder.Property(o => o.IsActive)
            .IsRequired()
            .HasDefaultValue(true)
            .HasSentinel(true);

        builder.Property(o => o.CreatedAt).IsRequired();

        builder.HasIndex(o => new { o.TargetType, o.TargetId })
            .IsUnique()
            .HasDatabaseName("IX_shift_obligations_target_unique");
    }
}
