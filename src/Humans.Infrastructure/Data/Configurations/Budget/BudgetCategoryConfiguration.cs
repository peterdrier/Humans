using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Budget;

public class BudgetCategoryConfiguration : IEntityTypeConfiguration<BudgetCategory>
{
    public void Configure(EntityTypeBuilder<BudgetCategory> builder)
    {
        builder.ToTable("budget_categories");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(256).IsRequired();
        builder.Property(c => c.Slug).HasMaxLength(64).IsRequired();
        builder.Property(c => c.AllocatedAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(c => c.ExpenditureType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(c => c.SortOrder).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

#pragma warning disable CS0618 // Obsolete cross-domain nav kept so EF FK constraint stays modelled.
        builder.HasOne(c => c.Team).WithMany().HasForeignKey(c => c.TeamId).OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618
        builder.HasMany(c => c.LineItems).WithOne(l => l.BudgetCategory).HasForeignKey(l => l.BudgetCategoryId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.BudgetGroupId, c.SortOrder });
        builder.HasIndex(c => new { c.BudgetGroupId, c.Slug }).IsUnique();
        builder.HasIndex(c => c.TeamId).HasFilter("\"TeamId\" IS NOT NULL");
    }
}
