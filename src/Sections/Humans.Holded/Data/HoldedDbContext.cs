using Humans.Holded.Data.Configurations;
using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Holded.Data;

/// <summary>
/// Per-section database context for the Holded mirror: maps only
/// <c>holded_ledger_lines</c>, <c>holded_sync_states</c>, <c>holded_accounts</c> and
/// <c>holded_api_calls</c>, with its own <c>__EFMigrationsHistory_Holded</c> table and
/// migrations under <c>Data/Migrations/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <c>FinanceDbContext</c> (issue #750): the repository is the only
/// consumer. Configurations are applied explicitly (not by assembly scanning) so this model
/// can never accrete another section's tables.
/// </remarks>
internal sealed class HoldedDbContext(DbContextOptions<HoldedDbContext> options)
    : DbContext(options)
{
    public DbSet<HoldedLedgerLine> HoldedLedgerLines => Set<HoldedLedgerLine>();
    public DbSet<HoldedSyncState> HoldedSyncStates => Set<HoldedSyncState>();
    public DbSet<HoldedAccount> HoldedAccounts => Set<HoldedAccount>();
    public DbSet<HoldedApiCall> HoldedApiCalls => Set<HoldedApiCall>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new HoldedLedgerLineConfiguration());
        builder.ApplyConfiguration(new HoldedSyncStateConfiguration());
        builder.ApplyConfiguration(new HoldedAccountConfiguration());
        builder.ApplyConfiguration(new HoldedApiCallConfiguration());
    }
}
