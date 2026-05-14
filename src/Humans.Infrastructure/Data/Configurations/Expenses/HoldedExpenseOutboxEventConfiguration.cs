using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class HoldedExpenseOutboxEventConfiguration
    : IEntityTypeConfiguration<HoldedExpenseOutboxEvent>
{
    public void Configure(EntityTypeBuilder<HoldedExpenseOutboxEvent> b)
    {
        b.ToTable("holded_expense_outbox_events");
        b.HasKey(x => x.Id);

        b.Property(x => x.EventType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        b.Property(x => x.LastError).HasMaxLength(2000);

        b.HasIndex(x => x.ExpenseReportId);
        b.HasIndex(x => new { x.ProcessedAt, x.FailedPermanently });
    }
}
