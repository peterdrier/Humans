using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("store_invoices");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedDocId).IsUnique();
        b.Property(x => x.HoldedDocId).HasMaxLength(100);
        b.Property(x => x.HoldedDocNumber).HasMaxLength(50);
        b.Property(x => x.RequestPayload).HasColumnType("jsonb");
        b.Property(x => x.ResponsePayload).HasColumnType("jsonb");

        // Intra-section FK to Order — one invoice per order, typed-FK form, no nav.
        b.HasOne<Order>()
            .WithOne()
            .HasForeignKey<Invoice>(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
