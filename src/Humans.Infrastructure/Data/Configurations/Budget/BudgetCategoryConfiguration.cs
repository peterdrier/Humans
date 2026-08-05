using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Budget;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class BudgetCategoryConfiguration : IEntityTypeConfiguration<BudgetCategory>
{
    public void Configure(EntityTypeBuilder<BudgetCategory> builder)
    {
        builder.ToTable("budget_categories");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(256).IsRequired();
        builder.Property(c => c.AllocatedAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(c => c.ExpenditureType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(c => c.SortOrder).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasOne<Team>().WithMany().HasForeignKey(c => c.TeamId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(c => c.LineItems).WithOne(l => l.BudgetCategory).HasForeignKey(l => l.BudgetCategoryId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.BudgetGroupId, c.SortOrder });
        builder.HasIndex(c => c.TeamId).HasFilter("\"TeamId\" IS NOT NULL");
    }
}
