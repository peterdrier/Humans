using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetLineItemConfiguration : IEntityTypeConfiguration<BudgetLineItem>
{
    public void Configure(EntityTypeBuilder<BudgetLineItem> builder)
    {
        builder.ToTable("budget_line_items");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Description).HasMaxLength(500).IsRequired();
        builder.Property(l => l.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(l => l.Notes).HasMaxLength(2000);
        builder.Property(l => l.SortOrder).IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();

        builder.HasOne(l => l.ResponsibleTeam).WithMany().HasForeignKey(l => l.ResponsibleTeamId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(l => new { l.BudgetCategoryId, l.SortOrder });
        builder.HasIndex(l => l.ResponsibleTeamId).HasFilter("\"ResponsibleTeamId\" IS NOT NULL");
    }
}
