using Humans.Stripe.Data.Configurations;
using Humans.Stripe.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Stripe.Data;

internal sealed class StripeDbContext(DbContextOptions<StripeDbContext> options)
    : DbContext(options)
{
    public DbSet<StripeBalanceTransaction> StripeBalanceTransactions => Set<StripeBalanceTransaction>();
    public DbSet<StripePayout> StripePayouts => Set<StripePayout>();
    public DbSet<StripeBalance> StripeBalances => Set<StripeBalance>();
    public DbSet<StripeSyncState> StripeSyncStates => Set<StripeSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new StripeBalanceTransactionConfiguration());
        builder.ApplyConfiguration(new StripePayoutConfiguration());
        builder.ApplyConfiguration(new StripeBalanceConfiguration());
        builder.ApplyConfiguration(new StripeSyncStateConfiguration());
    }
}
