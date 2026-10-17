using Humans.Preapprovals.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Preapprovals.Data.Configurations;

internal sealed class PreapprovalProgramConfiguration : IEntityTypeConfiguration<PreapprovalProgram>
{
    public void Configure(EntityTypeBuilder<PreapprovalProgram> b)
    {
        b.ToTable("pre_programs");
        b.HasKey(p => p.Id);

        b.Property(p => p.Name).IsRequired().HasMaxLength(200);
        b.Property(p => p.Description).HasMaxLength(2000);
        b.Property(p => p.Kind).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.PerPersonAmount).HasColumnType("decimal(18,2)");
        b.Property(p => p.TotalCeiling).HasColumnType("decimal(18,2)");
        b.Property(p => p.HoldedAccountNumber).IsRequired();
        b.Property(p => p.HoldedAccountId).IsRequired().HasMaxLength(64);
        b.Property(p => p.AcceptsApplications).IsRequired();
        b.Property(p => p.ClaimBy).IsRequired();
        b.Property(p => p.Status).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.CreatedByUserId).IsRequired();
        b.Property(p => p.CreatedAt).IsRequired();
        b.Property(p => p.UpdatedAt).IsRequired();

        b.HasMany(p => p.Managers)
            .WithOne(m => m.Program)
            .HasForeignKey(m => m.ProgramId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(p => p.Grants)
            .WithOne(g => g.Program)
            .HasForeignKey(g => g.ProgramId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(p => p.Status);
    }
}

internal sealed class ProgramManagerConfiguration : IEntityTypeConfiguration<ProgramManager>
{
    public void Configure(EntityTypeBuilder<ProgramManager> b)
    {
        b.ToTable("pre_program_managers");
        // One role; a manager is a manager.
        b.HasKey(m => new { m.ProgramId, m.UserId });

        // UserId is a bare cross-section reference: indexed for the per-user reads, no FK.
        b.Property(m => m.UserId).IsRequired();
        b.Property(m => m.AddedByUserId).IsRequired();
        b.Property(m => m.AddedAt).IsRequired();

        b.HasIndex(m => m.UserId);
    }
}

internal sealed class PreapprovalGrantConfiguration : IEntityTypeConfiguration<PreapprovalGrant>
{
    public void Configure(EntityTypeBuilder<PreapprovalGrant> b)
    {
        b.ToTable("pre_grants");
        b.HasKey(g => g.Id);

        b.Property(g => g.ProgramId).IsRequired();
        // BeneficiaryUserId and ExpenseReportId are bare cross-section references, no FK.
        b.Property(g => g.BeneficiaryUserId).IsRequired();
        b.Property(g => g.Label).HasMaxLength(200);
        b.Property(g => g.MaxAmount).IsRequired().HasColumnType("decimal(18,2)");
        b.Property(g => g.Status).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(g => g.ApplicationNote).HasMaxLength(2000);
        b.Property(g => g.RequestedAmount).HasColumnType("decimal(18,2)");
        b.Property(g => g.CreatedAt).IsRequired();
        b.Property(g => g.UpdatedAt).IsRequired();

        b.HasIndex(g => g.BeneficiaryUserId);
        b.HasIndex(g => new { g.ProgramId, g.Status });
        b.HasIndex(g => g.ExpenseReportId);
    }
}

internal sealed class ReminderSentConfiguration : IEntityTypeConfiguration<ReminderSent>
{
    public void Configure(EntityTypeBuilder<ReminderSent> b)
    {
        b.ToTable("pre_reminders_sent");
        // Keyed by the deadline announced: a moved deadline runs the reminders again.
        b.HasKey(r => new { r.GrantId, r.Kind, r.ClaimBy });

        b.Property(r => r.Kind).IsRequired().HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.SentAt).IsRequired();

        b.HasOne<PreapprovalGrant>()
            .WithMany()
            .HasForeignKey(r => r.GrantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
