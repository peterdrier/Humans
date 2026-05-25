using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedExpenseDocConfiguration : IEntityTypeConfiguration<HoldedExpenseDoc>
{
    public void Configure(EntityTypeBuilder<HoldedExpenseDoc> b)
    {
        b.ToTable("holded_expense_docs");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedDocId).IsUnique();
        b.HasIndex(x => x.BudgetCategoryId);
        b.HasIndex(x => x.MatchStatus);
        b.HasIndex(x => x.Date);
        b.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.MatchSource).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.HoldedDocId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.TagsJson).HasColumnType("jsonb");
        b.Property(x => x.RawPayload).HasColumnType("jsonb");
    }
}
