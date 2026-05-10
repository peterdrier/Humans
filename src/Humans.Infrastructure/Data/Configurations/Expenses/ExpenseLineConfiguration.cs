using Humans.Domain.Entities;
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

        b.HasOne(x => x.Attachment)
            .WithMany()
            .HasForeignKey(x => x.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ExpenseReportId);
    }
}
