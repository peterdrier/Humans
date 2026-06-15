using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedCreditorContactConfiguration : IEntityTypeConfiguration<HoldedCreditorContact>
{
    public void Configure(EntityTypeBuilder<HoldedCreditorContact> b)
    {
        b.ToTable("holded_creditor_contacts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasIndex(x => x.SupplierAccountNum);
        b.Property(x => x.HoldedContactId).HasMaxLength(64);
        b.Property(x => x.Source).IsRequired().HasConversion<string>().HasMaxLength(16);
    }
}
