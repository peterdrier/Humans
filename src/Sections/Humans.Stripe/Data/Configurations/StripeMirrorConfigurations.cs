using Humans.Stripe.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Stripe.Data.Configurations;

internal sealed class StripeBalanceTransactionConfiguration : IEntityTypeConfiguration<StripeBalanceTransaction>
{
    public void Configure(EntityTypeBuilder<StripeBalanceTransaction> b)
    {
        b.ToTable("stripe_balance_transactions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.AccountId).HasMaxLength(64);
        b.Property(x => x.Type).HasMaxLength(64);
        b.Property(x => x.ReportingCategory).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.SourceId).HasMaxLength(64);
        b.Property(x => x.PayoutId).HasMaxLength(64);
        b.Property(x => x.PaymentIntentId).HasMaxLength(64);
        b.Property(x => x.CustomerName).HasMaxLength(256);
        b.Property(x => x.CustomerEmail).HasMaxLength(256);
        b.Property(x => x.Metadata).HasColumnType("jsonb");
        b.Property(x => x.Raw).HasColumnType("jsonb");
        b.HasIndex(x => x.AccountId);
    }
}

internal sealed class StripePayoutConfiguration : IEntityTypeConfiguration<StripePayout>
{
    public void Configure(EntityTypeBuilder<StripePayout> b)
    {
        b.ToTable("stripe_payouts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.AccountId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.Method).HasMaxLength(32);
        b.Property(x => x.BalanceTransactionId).HasMaxLength(64);
        b.Property(x => x.FailureCode).HasMaxLength(64);
        b.Property(x => x.FailureMessage).HasMaxLength(1000);
        b.Property(x => x.StatementDescriptor).HasMaxLength(64);
        b.HasIndex(x => x.AccountId);
    }
}

internal sealed class StripeBalanceConfiguration : IEntityTypeConfiguration<StripeBalance>
{
    public void Configure(EntityTypeBuilder<StripeBalance> b)
    {
        b.ToTable("stripe_balances");
        b.HasKey(x => new { x.AccountId, x.Currency });
        b.Property(x => x.AccountId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
    }
}

internal sealed class StripeSyncStateConfiguration : IEntityTypeConfiguration<StripeSyncState>
{
    public void Configure(EntityTypeBuilder<StripeSyncState> b)
    {
        b.ToTable("stripe_sync_states");
        b.HasKey(x => x.AccountId);
        b.Property(x => x.AccountId).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
