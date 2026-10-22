using Microsoft.EntityFrameworkCore;
using Humans.Expenses.Data.Configurations;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

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
