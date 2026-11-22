using Humans.Development.Data.Configurations;
using Humans.Development.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Development.Data;

/// <summary>Registered outside Production only, so the demo ledger's table never exists there.</summary>
internal sealed class DevelopmentDbContext(DbContextOptions<DevelopmentDbContext> options) : DbContext(options)
{
    public DbSet<DemoLedgerEntry> DemoLedger => Set<DemoLedgerEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new DemoLedgerEntryConfiguration());
    }
}
