using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Settings.Domain;

namespace Humans.Settings.Data.Configurations;

internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        // Physical rename to "settings" is deferred to the retirement step; not authorized here.
        builder.ToTable("system_settings");

        builder.HasKey(e => e.Key);

        builder.Property(e => e.Key)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.Value)
            .HasMaxLength(1000)
            .IsRequired();

        builder.HasData(
            new Setting { Key = "IsEmailSendingPaused", Value = "false" }
        );
    }
}
