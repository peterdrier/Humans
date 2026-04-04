using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetYearConfiguration : IEntityTypeConfiguration<BudgetYear>
{
    public void Configure(EntityTypeBuilder<BudgetYear> builder)
    {
        builder.ToTable("budget_years");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Year).HasMaxLength(50).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(256).IsRequired();
        builder.Property(b => b.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.Property(b => b.UpdatedAt).IsRequired();
        builder.Property(b => b.DeletedAt);

        builder.HasMany(b => b.Groups).WithOne(g => g.BudgetYear).HasForeignKey(g => g.BudgetYearId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(b => b.AuditLogs).WithOne(a => a.BudgetYear).HasForeignKey(a => a.BudgetYearId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => b.Year).IsUnique();
        builder.HasIndex(b => b.Status);
    }
}
