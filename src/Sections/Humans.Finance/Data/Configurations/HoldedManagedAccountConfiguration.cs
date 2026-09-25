using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class HoldedManagedAccountConfiguration : IEntityTypeConfiguration<HoldedManagedAccount>
{
    public void Configure(EntityTypeBuilder<HoldedManagedAccount> b)
    {
        b.ToTable("holded_managed_accounts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedAccountNumber).IsUnique();
        b.HasIndex(x => x.HoldedAccountId).IsUnique();
        b.Property(x => x.HoldedAccountId).HasMaxLength(64);
        b.Property(x => x.Label).HasMaxLength(200);
    }
}
