using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Gate;

public sealed class GateStaffPinConfiguration : IEntityTypeConfiguration<GateStaffPin>
{
    public void Configure(EntityTypeBuilder<GateStaffPin> builder)
    {
        builder.ToTable("gate_staff_pins");

        // One PIN per Humans user — UserId is the key (a bare cross-section id, not an FK).
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).ValueGeneratedNever();

        builder.Property(x => x.PinHash)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Override-authority flag. Plain IsRequired (no HasDefaultValue — that bool-sentinel makes EF
        // omit a `false` from writes). The migration's AddColumn supplies the one-time backfill default
        // for existing rows; every write sets the value explicitly from the entity.
        builder.Property(x => x.AdminEnrolled).IsRequired();
    }
}
