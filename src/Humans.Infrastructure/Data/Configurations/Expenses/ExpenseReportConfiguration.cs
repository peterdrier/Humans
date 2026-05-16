using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class ExpenseReportConfiguration : IEntityTypeConfiguration<ExpenseReport>
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
        b.Property(x => x.PayeeName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PayeeIban).HasMaxLength(34).IsRequired();
        b.Property(x => x.Total).HasColumnType("decimal(12,2)");
        b.Property(x => x.LastRejectionReason).HasMaxLength(1000);
        b.Property(x => x.HoldedDocId).HasMaxLength(64);

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
