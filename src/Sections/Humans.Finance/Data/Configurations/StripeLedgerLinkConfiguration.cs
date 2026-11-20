using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class StripeLedgerLinkConfiguration : IEntityTypeConfiguration<StripeLedgerLink>
{
    public void Configure(EntityTypeBuilder<StripeLedgerLink> b)
    {
        b.ToTable("finance_stripe_ledger_links");
        b.HasKey(x => x.LedgerAccountNum);
        b.Property(x => x.LedgerAccountNum).ValueGeneratedNever();
        b.Property(x => x.StripeAccountId).HasMaxLength(64);
    }
}
