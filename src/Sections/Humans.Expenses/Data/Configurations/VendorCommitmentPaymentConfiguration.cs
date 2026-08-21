using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

internal sealed class VendorCommitmentPaymentConfiguration
    : IEntityTypeConfiguration<VendorCommitmentPayment>
{
    public void Configure(EntityTypeBuilder<VendorCommitmentPayment> b)
    {
        b.ToTable("vendor_commitment_payments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Amount).HasColumnType("decimal(12,2)");
        b.Property(x => x.Reference).HasMaxLength(200);

        b.HasIndex(x => x.VendorCommitmentId);
    }
}
