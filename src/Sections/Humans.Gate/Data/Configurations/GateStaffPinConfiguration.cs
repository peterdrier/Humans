using Humans.Gate.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Gate.Data.Configurations;

internal sealed class GateStaffPinConfiguration : IEntityTypeConfiguration<GateStaffPin>
{
    public void Configure(EntityTypeBuilder<GateStaffPin> builder)
    {
        builder.ToTable("gate_staff_pins");

        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).ValueGeneratedNever();

        builder.Property(x => x.PinHash)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // No HasDefaultValue: that bool sentinel makes EF omit a false from writes.
        builder.Property(x => x.AdminEnrolled).IsRequired();
    }
}
