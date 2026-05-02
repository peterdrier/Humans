using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace Humans.Infrastructure.Data.Configurations;

public class AgentSettingsConfiguration : IEntityTypeConfiguration<AgentSettings>
{
    public void Configure(EntityTypeBuilder<AgentSettings> builder)
    {
        // Singleton: CHECK ("Id" = 1) declared here so EF generates
        // CreateCheckConstraint/DropCheckConstraint in the migration — no raw
        // migrationBuilder.Sql() (per memory/architecture/no-hand-edited-migrations.md).
        builder.ToTable("agent_settings", t =>
            t.HasCheckConstraint("ck_agent_settings_singleton", "\"Id\" = 1"));

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Model).HasMaxLength(64).IsRequired();
        builder.Property(s => s.PreloadConfig).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // Seed the singleton row with disabled defaults so the app is always queryable.
        builder.HasData(new AgentSettings
        {
            Id = 1,
            Enabled = false,
            Model = "claude-sonnet-4-6",
            PreloadConfig = AgentPreloadConfig.Tier1,
            DailyMessageCap = 30,
            HourlyMessageCap = 10,
            DailyTokenCap = 50000,
            RetentionDays = 90,
            UpdatedAt = Instant.FromUtc(2026, 4, 21, 0, 0)
        });

    }
}
