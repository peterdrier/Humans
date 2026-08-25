using Humans.Finance.Domain;
using Humans.Finance.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Finance.Data;

/// <summary>
/// Finance's own context (nobodies-collective/Humans#858): the four holded_* tables and the two
/// sepa_payout_* tables it owns, its own
/// <c>__EFMigrationsHistory_Finance</c>, same database and connection. Configurations are applied
/// explicitly, never by assembly scan, so this model cannot accrete another section's tables.
/// </summary>
internal sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options)
    : DbContext(options)
{
    public DbSet<HoldedExpenseDoc> HoldedExpenseDocs => Set<HoldedExpenseDoc>();
    public DbSet<HoldedCategoryMap> HoldedCategoryMap => Set<HoldedCategoryMap>();
    public DbSet<HoldedCreditorContact> HoldedCreditorContacts => Set<HoldedCreditorContact>();
    public DbSet<HoldedDocSyncState> HoldedDocSyncStates => Set<HoldedDocSyncState>();
    public DbSet<SepaPayoutFile> SepaPayoutFiles => Set<SepaPayoutFile>();
    public DbSet<SepaPayoutTransfer> SepaPayoutTransfers => Set<SepaPayoutTransfer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new HoldedExpenseDocConfiguration());
        builder.ApplyConfiguration(new HoldedCategoryMapConfiguration());
        builder.ApplyConfiguration(new HoldedCreditorContactConfiguration());
        builder.ApplyConfiguration(new HoldedDocSyncStateConfiguration());
        builder.ApplyConfiguration(new SepaPayoutFileConfiguration());
        builder.ApplyConfiguration(new SepaPayoutTransferConfiguration());
    }
}
