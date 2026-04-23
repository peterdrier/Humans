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

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Shift)
            .WithMany(s => s.ShiftSignups)
            .HasForeignKey(d => d.ShiftId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.EnrolledByUser)
            .WithMany()
            .HasForeignKey(d => d.EnrolledByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(d => d.ReviewedByUser)
            .WithMany()
            .HasForeignKey(d => d.ReviewedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
