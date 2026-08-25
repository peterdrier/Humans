using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class SepaPayoutFileConfiguration : IEntityTypeConfiguration<SepaPayoutFile>
{
    public void Configure(EntityTypeBuilder<SepaPayoutFile> b)
    {
        b.ToTable("sepa_payout_files");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.GeneratedAt);
        b.Property(x => x.FileName).HasMaxLength(128);
        b.Property(x => x.Checksum).HasMaxLength(64);
    }
}
