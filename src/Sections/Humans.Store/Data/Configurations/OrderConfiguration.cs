using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("store_orders");
        b.HasKey(x => x.Id);
#pragma warning disable CS0618 // Label is [Obsolete] (#816); the column stays mapped so no migration is needed.
        b.Property(x => x.Label).HasMaxLength(100);
#pragma warning restore CS0618
        b.Property(x => x.CounterpartyName).HasMaxLength(200);
        b.Property(x => x.CounterpartyVatId).HasMaxLength(50);
        b.Property(x => x.CounterpartyAddress).HasMaxLength(500);
        b.Property(x => x.CounterpartyCountryCode).HasMaxLength(2);
        b.Property(x => x.CounterpartyEmail).HasMaxLength(320);
        b.Property(x => x.State).HasConversion<int>();
        b.Property(x => x.Year).IsRequired();
        b.HasIndex(x => x.State);
        b.HasIndex(x => x.CampSeasonId);
        b.HasIndex(x => x.TeamId);
        b.HasMany(x => x.Lines).WithOne(l => l.Order!).HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Payments).WithOne(p => p.Order!).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
