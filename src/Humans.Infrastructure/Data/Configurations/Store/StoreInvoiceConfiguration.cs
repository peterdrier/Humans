using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreInvoiceConfiguration : IEntityTypeConfiguration<StoreInvoice>
{
    public void Configure(EntityTypeBuilder<StoreInvoice> b)
    {
        b.ToTable("store_invoices");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedDocId).IsUnique();
        b.Property(x => x.HoldedDocId).HasMaxLength(100);
        b.Property(x => x.HoldedDocNumber).HasMaxLength(50);
        b.Property(x => x.RequestPayload).HasColumnType("jsonb");
        b.Property(x => x.ResponsePayload).HasColumnType("jsonb");

        // Intra-section FK to StoreOrder — one invoice per order, typed-FK form, no nav.
        b.HasOne<StoreOrder>()
            .WithOne()
            .HasForeignKey<StoreInvoice>(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
