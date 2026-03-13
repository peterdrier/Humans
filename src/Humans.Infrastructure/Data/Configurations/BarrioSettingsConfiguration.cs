using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class BarrioSettingsConfiguration : IEntityTypeConfiguration<BarrioSettings>
{
    public void Configure(EntityTypeBuilder<BarrioSettings> builder)
    {
        builder.ToTable("barrio_settings");

        builder.Property(s => s.OpenSeasons).HasColumnType("jsonb");

        builder.HasData(new BarrioSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
            PublicYear = 2026,
            OpenSeasons = new List<int> { 2026 }
        });
    }
}
