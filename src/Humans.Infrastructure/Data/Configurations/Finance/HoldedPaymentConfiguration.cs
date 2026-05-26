using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedPaymentConfiguration : IEntityTypeConfiguration<HoldedPayment>
{
    public void Configure(EntityTypeBuilder<HoldedPayment> b)
    {
        b.ToTable("holded_payments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedPaymentId).IsUnique();
        b.HasIndex(x => x.HoldedContactId);
        b.Property(x => x.HoldedPaymentId).HasMaxLength(64);
        b.Property(x => x.HoldedContactId).HasMaxLength(64);
        b.Property(x => x.DocumentType).HasMaxLength(32);
        b.Property(x => x.Amount).HasColumnType("decimal(12,2)");
    }
}
