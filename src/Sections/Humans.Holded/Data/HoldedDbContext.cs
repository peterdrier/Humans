using Humans.Holded.Data.Configurations;
using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Holded.Data;

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
