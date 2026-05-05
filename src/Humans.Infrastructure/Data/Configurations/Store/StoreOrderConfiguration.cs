using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreOrderConfiguration : IEntityTypeConfiguration<StoreOrder>
{
    public void Configure(EntityTypeBuilder<StoreOrder> b)
    {
        b.ToTable("store_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(100);
        b.Property(x => x.CounterpartyName).HasMaxLength(200);
        b.Property(x => x.CounterpartyVatId).HasMaxLength(50);
        b.Property(x => x.CounterpartyAddress).HasMaxLength(500);
        b.Property(x => x.CounterpartyCountryCode).HasMaxLength(2);
        b.Property(x => x.CounterpartyEmail).HasMaxLength(320);
        b.Property(x => x.State).HasConversion<int>();
        b.HasIndex(x => x.State);
        b.HasIndex(x => x.CampSeasonId);
        b.HasMany(x => x.Lines).WithOne(l => l.Order!).HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Payments).WithOne(p => p.Order!).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
