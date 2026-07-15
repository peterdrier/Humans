using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Finance;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Finance section
/// (nobodies-collective/Humans#858): maps only <c>holded_expense_docs</c>,
/// <c>holded_category_map</c>, <c>holded_ledger_lines</c>,
/// <c>holded_creditor_contacts</c> and <c>holded_sync_states</c>, with its own
/// <c>__EFMigrationsHistory_Finance</c> table and migrations under
/// <c>Migrations/Finance/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
internal sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options)
    : DbContext(options)
{
    public DbSet<HoldedExpenseDoc> HoldedExpenseDocs => Set<HoldedExpenseDoc>();
    public DbSet<HoldedCategoryMap> HoldedCategoryMap => Set<HoldedCategoryMap>();
    public DbSet<HoldedLedgerLine> HoldedLedgerLines => Set<HoldedLedgerLine>();
    public DbSet<HoldedCreditorContact> HoldedCreditorContacts => Set<HoldedCreditorContact>();
    public DbSet<HoldedSyncState> HoldedSyncStates => Set<HoldedSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new HoldedExpenseDocConfiguration());
        builder.ApplyConfiguration(new HoldedCategoryMapConfiguration());
        builder.ApplyConfiguration(new HoldedLedgerLineConfiguration());
        builder.ApplyConfiguration(new HoldedCreditorContactConfiguration());
        builder.ApplyConfiguration(new HoldedSyncStateConfiguration());
    }
}
