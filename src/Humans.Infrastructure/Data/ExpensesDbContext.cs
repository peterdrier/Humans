using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Expenses;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Expenses section
/// (nobodies-collective/Humans#858): maps only <c>expense_reports</c>,
/// <c>expense_lines</c>, <c>expense_attachments</c> and
/// <c>holded_expense_outbox_events</c>, with its own
/// <c>__EFMigrationsHistory_Expenses</c> table and migrations under
/// <c>Migrations/Expenses/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
internal sealed class ExpensesDbContext(DbContextOptions<ExpensesDbContext> options)
    : DbContext(options)
{
    public DbSet<ExpenseReport> ExpenseReports => Set<ExpenseReport>();
    public DbSet<ExpenseLine> ExpenseLines => Set<ExpenseLine>();
    public DbSet<ExpenseAttachment> ExpenseAttachments => Set<ExpenseAttachment>();
    public DbSet<HoldedExpenseOutboxEvent> HoldedExpenseOutboxEvents => Set<HoldedExpenseOutboxEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ExpenseReportConfiguration());
        builder.ApplyConfiguration(new ExpenseLineConfiguration());
        builder.ApplyConfiguration(new ExpenseAttachmentConfiguration());
        builder.ApplyConfiguration(new HoldedExpenseOutboxEventConfiguration());
    }
}
