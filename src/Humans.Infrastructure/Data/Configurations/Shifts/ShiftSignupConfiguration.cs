using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

public class ShiftSignupConfiguration : IEntityTypeConfiguration<ShiftSignup>
{
    public void Configure(EntityTypeBuilder<ShiftSignup> builder)
    {
        builder.ToTable("shift_signups");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(d => d.StatusReason).HasMaxLength(1000);

        builder.Property(e => e.SignupBlockId);
        builder.HasIndex(e => e.SignupBlockId);

        builder.HasIndex(d => d.UserId);
        builder.HasIndex(d => d.ShiftId);
        builder.HasIndex(d => new { d.ShiftId, d.Status });

        // UserId / EnrolledByUserId / ReviewedByUserId are bare cross-section Guid
        // columns — no FK constraint, no nav.

        builder.HasOne(d => d.Shift)
            .WithMany(s => s.ShiftSignups)
            .HasForeignKey(d => d.ShiftId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
