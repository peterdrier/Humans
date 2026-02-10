using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Data.Configurations;

public class ApplicationConfiguration : IEntityTypeConfiguration<MemberApplication>
{
    public void Configure(EntityTypeBuilder<MemberApplication> builder)
    {
        builder.ToTable("applications");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Status)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(a => a.Motivation)
            .HasMaxLength(4000)
            .IsRequired();

        builder.Property(a => a.AdditionalInfo)
            .HasMaxLength(4000);

        builder.Property(a => a.ReviewNotes)
            .HasMaxLength(4000);

        builder.Property(a => a.SubmittedAt)
            .IsRequired();

        builder.Property(a => a.UpdatedAt)
            .IsRequired();

        builder.HasOne(a => a.ReviewedByUser)
            .WithMany()
            .HasForeignKey(a => a.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.StateHistory)
            .WithOne(sh => sh.Application)
            .HasForeignKey(sh => sh.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => a.SubmittedAt);
        builder.HasIndex(a => new { a.UserId, a.Status });

        // Ignore the state machine property
        builder.Ignore(a => a.StateMachine);
    }
}
