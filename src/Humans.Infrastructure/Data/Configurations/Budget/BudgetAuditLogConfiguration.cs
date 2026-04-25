using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Budget;

public class BudgetAuditLogConfiguration : IEntityTypeConfiguration<BudgetAuditLog>
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

#pragma warning disable CS0618 // Obsolete cross-domain nav kept so EF FK constraint stays modelled.
        builder.HasOne(a => a.ActorUser).WithMany().HasForeignKey(a => a.ActorUserId).OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618

        builder.HasIndex(a => a.BudgetYearId);
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
