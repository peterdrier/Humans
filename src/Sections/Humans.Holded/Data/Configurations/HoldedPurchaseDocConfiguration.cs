using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Holded.Data.Configurations;

internal sealed class HoldedPurchaseDocConfiguration : IEntityTypeConfiguration<HoldedPurchaseDoc>
{
    public void Configure(EntityTypeBuilder<HoldedPurchaseDoc> b)
    {
        b.ToTable("holded_purchase_docs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.ContactId).HasMaxLength(64);
        b.Property(x => x.BookedAccountId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Subtotal).HasColumnType("decimal(14,2)");
        b.Property(x => x.Tax).HasColumnType("decimal(14,2)");
        b.Property(x => x.Total).HasColumnType("decimal(14,2)");
        b.Property(x => x.PaymentsPending).HasColumnType("decimal(14,2)");
    }
}
