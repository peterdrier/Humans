using Humans.Settings.Data.Configurations;
using Humans.Settings.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Settings.Data;

/// <summary>
/// Per-section database context for the Settings section
/// (nobodies-collective/Humans#858): maps <c>system_settings</c> (the app-wide
/// key/value store) and <c>settings_event</c> (the typed app-wide event settings),
/// with its own <c>__EFMigrationsHistory_Settings</c> table and migrations under
/// <c>Data/Migrations/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories are the
/// only consumers. Configurations are applied explicitly (not by assembly scanning)
/// so this model can never accrete another section's tables.
/// </remarks>
internal sealed class SettingsDbContext(DbContextOptions<SettingsDbContext> options)
    : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<EventSettings> EventSettings => Set<EventSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new SettingConfiguration());
        builder.ApplyConfiguration(new EventSettingsConfiguration());
    }
}
