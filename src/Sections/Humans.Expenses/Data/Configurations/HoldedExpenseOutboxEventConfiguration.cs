using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class HoldedExpenseOutboxEventConfiguration
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
        // The drain now also filters on RetryCount and NextRetryAt; both ride along on the
        // existing (ProcessedAt, FailedPermanently) index at this scale (a few hundred rows).
    }
}
