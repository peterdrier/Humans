using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Camps.Data.Configurations;

internal sealed class CampSettingsConfiguration : IEntityTypeConfiguration<CampSettings>
{
    public void Configure(EntityTypeBuilder<CampSettings> builder)
    {
        builder.ToTable("camp_settings");

        // Reserved GUID block: 0010. See docs/guid-reservations.md.
        builder.HasData(new CampSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0010-000000000001")
        });
    }
}
