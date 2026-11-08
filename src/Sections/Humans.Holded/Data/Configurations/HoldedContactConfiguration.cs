using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Holded.Data.Configurations;

internal sealed class HoldedContactConfiguration : IEntityTypeConfiguration<HoldedContact>
{
    public void Configure(EntityTypeBuilder<HoldedContact> b)
    {
        b.ToTable("holded_contacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.Iban).HasMaxLength(64);
        b.Property(x => x.TaxCode).HasMaxLength(64);
    }
}
