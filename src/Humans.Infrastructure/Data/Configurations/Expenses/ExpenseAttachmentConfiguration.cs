using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class ExpenseAttachmentConfiguration : IEntityTypeConfiguration<ExpenseAttachment>
{
    public void Configure(EntityTypeBuilder<ExpenseAttachment> b)
    {
        b.ToTable("expense_attachments");
        b.HasKey(x => x.Id);

        b.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.Extension).HasMaxLength(8).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
    }
}
