using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class ExpenseReportConfiguration : IEntityTypeConfiguration<ExpenseReport>
{
    public void Configure(EntityTypeBuilder<ExpenseReport> b)
    {
        b.ToTable("expense_reports");
        b.HasKey(x => x.Id);

        b.Property(x => x.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        b.Property(x => x.Note).HasMaxLength(500);
        // Rows that predate the account column keep 0 (declared here so model and DB agree);
        // their booking still derives from the category map at push time.
        b.Property(x => x.HoldedAccountNumber).IsRequired().HasDefaultValue(0);
        b.Property(x => x.HoldedAccountId).HasMaxLength(64);
        b.Property(x => x.PayeeName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PayeeIban).HasMaxLength(34).IsRequired();
        b.Property(x => x.Total).HasColumnType("decimal(12,2)");
        b.Property(x => x.MaxAmount).HasColumnType("decimal(12,2)");
        b.Property(x => x.LastRejectionReason).HasMaxLength(1000);
        b.Property(x => x.HoldedDocId).HasMaxLength(64);
        b.Property(x => x.HoldedContactId).HasMaxLength(64);
        b.HasIndex(x => x.HoldedContactId);

        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.ExpenseReportId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK-only refs (no nav)
        b.HasIndex(x => new { x.SubmitterUserId, x.Status });
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.BudgetCategoryId);
        b.HasIndex(x => x.HoldedDocId);
    }
}
