using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class ExpenseLineConfiguration : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> b)
    {
        b.ToTable("expense_lines");
        b.HasKey(x => x.Id);

        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(12,2)");

        b.Property(x => x.HoldedDocId).HasMaxLength(64);
        b.Property(x => x.DeclinedReason).HasMaxLength(1000);
        b.Ignore(x => x.Declined);

        b.Property(x => x.LineType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(ExpenseLineType.Receipt);

        b.HasOne(x => x.Attachment)
            .WithMany()
            .HasForeignKey(x => x.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Proof rows point at their Invoice line. Restrict, not cascade: the service removes
        // proofs explicitly so their attachment rows + files are cleaned up too.
        b.HasOne<ExpenseLine>()
            .WithMany()
            .HasForeignKey(x => x.ParentLineId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ExpenseReportId);

        // One Holded document per line: a duplicate create can never be recorded. Postgres allows
        // many NULLs in a unique index, so unpushed lines are unaffected.
        b.HasIndex(x => x.HoldedDocId).IsUnique();
    }
}
