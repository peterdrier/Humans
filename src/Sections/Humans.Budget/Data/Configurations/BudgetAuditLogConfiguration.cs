using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Budget.Domain;

namespace Humans.Budget.Data.Configurations;

internal sealed class BudgetAuditLogConfiguration : IEntityTypeConfiguration<BudgetAuditLog>
{
    public void Configure(EntityTypeBuilder<BudgetAuditLog> builder)
    {
        builder.ToTable("budget_audit_logs");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.FieldName).HasMaxLength(100);
        builder.Property(a => a.OldValue).HasMaxLength(1000);
        builder.Property(a => a.NewValue).HasMaxLength(1000);
        builder.Property(a => a.Description).HasMaxLength(1000).IsRequired();
        builder.Property(a => a.OccurredAt).IsRequired();

        // Explicit index: replaces the convention index that died with the
        // ActorUserId FK. BudgetRepository:1013,1029 filter the column alone.
        builder.HasIndex(a => a.ActorUserId);

        builder.HasIndex(a => a.BudgetYearId);
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
