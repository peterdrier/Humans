using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class HoldedCategoryMapConfiguration : IEntityTypeConfiguration<HoldedCategoryMap>
{
    public void Configure(EntityTypeBuilder<HoldedCategoryMap> b)
    {
        b.ToTable("holded_category_map");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.BudgetCategoryId).IsUnique();
        b.HasIndex(x => x.HoldedAccountNumber).IsUnique();
        b.Property(x => x.HoldedAccountId).HasMaxLength(64);
        b.Property(x => x.Tag).HasMaxLength(128);
    }
}
