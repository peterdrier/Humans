using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class ExpenseLineConfiguration : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> b)
    {
        b.ToTable("expense_lines");
        b.HasKey(x => x.Id);

        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(12,2)");

        b.Property(x => x.LineType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(ExpenseLineType.Receipt);

        b.HasOne(x => x.Attachment)
            .WithMany()
            .HasForeignKey(x => x.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ExpenseReportId);
    }
}
