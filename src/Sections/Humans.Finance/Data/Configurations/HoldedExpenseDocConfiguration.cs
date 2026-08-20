using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class HoldedExpenseDocConfiguration : IEntityTypeConfiguration<HoldedExpenseDoc>
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
    }
}
