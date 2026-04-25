using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class CampHistoricalNameConfiguration : IEntityTypeConfiguration<CampHistoricalName>
{
    public void Configure(EntityTypeBuilder<CampHistoricalName> builder)
    {
        builder.ToTable("camp_historical_names");

        builder.Property(h => h.Name).HasMaxLength(256).IsRequired();
        builder.Property(h => h.Source).HasConversion<string>().HasMaxLength(50).IsRequired();
    }
}
