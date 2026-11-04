using Humans.Pleo.Data.Configurations;
using Humans.Pleo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Pleo.Data;

internal sealed class PleoDbContext(DbContextOptions<PleoDbContext> options)
    : DbContext(options)
{
    public DbSet<PleoEntry> PleoEntries => Set<PleoEntry>();
    public DbSet<PleoEmployee> PleoEmployees => Set<PleoEmployee>();
    public DbSet<PleoAccount> PleoAccounts => Set<PleoAccount>();
    public DbSet<PleoBalance> PleoBalances => Set<PleoBalance>();
    public DbSet<PleoSyncState> PleoSyncStates => Set<PleoSyncState>();
    public DbSet<PleoCategory> PleoCategories => Set<PleoCategory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new PleoEntryConfiguration());
        builder.ApplyConfiguration(new PleoEmployeeConfiguration());
        builder.ApplyConfiguration(new PleoAccountConfiguration());
        builder.ApplyConfiguration(new PleoBalanceConfiguration());
        builder.ApplyConfiguration(new PleoSyncStateConfiguration());
        builder.ApplyConfiguration(new PleoCategoryConfiguration());
    }
}
