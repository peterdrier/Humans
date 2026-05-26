using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedCreditorBalanceConfiguration : IEntityTypeConfiguration<HoldedCreditorBalance>
{
    public void Configure(EntityTypeBuilder<HoldedCreditorBalance> b)
    {
        b.ToTable("holded_creditor_balances");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.SupplierAccountNum).IsUnique();
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Balance).HasColumnType("decimal(12,2)");
    }
}
