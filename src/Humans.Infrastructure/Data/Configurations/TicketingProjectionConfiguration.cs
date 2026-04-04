using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class TicketingProjectionConfiguration : IEntityTypeConfiguration<TicketingProjection>
{
    public void Configure(EntityTypeBuilder<TicketingProjection> builder)
    {
        builder.ToTable("ticketing_projections");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.InitialSalesCount).IsRequired();
        builder.Property(p => p.DailySalesRate).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.AverageTicketPrice).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.VatRate).IsRequired();
        builder.Property(p => p.StripeFeePercent).HasPrecision(18, 4).IsRequired();
        builder.Property(p => p.StripeFeeFixed).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.TicketTailorFeePercent).HasPrecision(18, 4).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasIndex(p => p.BudgetGroupId).IsUnique();
    }
}
