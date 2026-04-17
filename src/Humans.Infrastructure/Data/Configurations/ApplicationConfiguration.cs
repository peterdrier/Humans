using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
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

        builder.Property(a => a.Language)
            .HasMaxLength(10);

        builder.Property(a => a.ReviewNotes)
            .HasMaxLength(4000);

        builder.Property(a => a.MembershipTier)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(a => a.DecisionNote)
            .HasMaxLength(4000);

        builder.Property(a => a.SubmittedAt)
            .IsRequired();

        builder.Property(a => a.UpdatedAt)
            .IsRequired();

        // FK-only relationships to User — the cross-domain nav properties
        // were stripped in the Governance migration (design-rules §6) and
        // the User-side User.Applications collection was stripped in the
        // User section migration Phase C3. The FK columns stay in the
        // schema; only the in-memory navs are removed.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.StateHistory)
            .WithOne(sh => sh.Application)
            .HasForeignKey(sh => sh.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.BoardVotes)
            .WithOne(bv => bv.Application)
            .HasForeignKey(bv => bv.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.Status);
        builder.HasIndex(a => a.SubmittedAt);
        builder.HasIndex(a => new { a.UserId, a.Status });
        builder.HasIndex(a => a.MembershipTier);

        // Ignore the state machine property
        builder.Ignore(a => a.StateMachine);
    }
}
