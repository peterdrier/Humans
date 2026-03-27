using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetGroupConfiguration : IEntityTypeConfiguration<BudgetGroup>
{
    public void Configure(EntityTypeBuilder<BudgetGroup> builder)
    {
        builder.ToTable("budget_groups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).HasMaxLength(256).IsRequired();
        builder.Property(g => g.SortOrder).IsRequired();
        builder.Property(g => g.IsRestricted).IsRequired();
        builder.Property(g => g.IsDepartmentGroup).IsRequired();
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt).IsRequired();

        builder.HasMany(g => g.Categories).WithOne(c => c.BudgetGroup).HasForeignKey(c => c.BudgetGroupId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => new { g.BudgetYearId, g.SortOrder });
    }
}
