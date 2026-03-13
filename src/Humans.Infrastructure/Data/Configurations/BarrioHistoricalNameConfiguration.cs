using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class BarrioHistoricalNameConfiguration : IEntityTypeConfiguration<BarrioHistoricalName>
{
    public void Configure(EntityTypeBuilder<BarrioHistoricalName> builder)
    {
        builder.ToTable("barrio_historical_names");

        builder.Property(h => h.Name).HasMaxLength(256).IsRequired();
        builder.Property(h => h.Source).HasConversion<string>().HasMaxLength(50).IsRequired();
    }
}
