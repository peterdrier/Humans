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
        builder.Property(a => a.Description).IsRequired();
        builder.Property(a => a.OccurredAt).IsRequired();

        // GetAuditLogEntriesForUserIdsAsync filters this column alone.
        builder.HasIndex(a => a.ActorUserId);

        builder.HasIndex(a => a.BudgetYearId);
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
