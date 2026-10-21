using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class PaymentDetailsConfiguration : IEntityTypeConfiguration<PaymentDetails>
{
    public void Configure(EntityTypeBuilder<PaymentDetails> b)
    {
        b.ToTable("finance_payment_details");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).ValueGeneratedNever();
        b.Property(x => x.Iban).HasMaxLength(34);
        b.Property(x => x.UpdatedAt).IsRequired();
    }
}
