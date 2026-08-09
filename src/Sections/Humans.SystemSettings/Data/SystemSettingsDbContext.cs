using Humans.SystemSettings.Data.Configurations;
using Humans.SystemSettings.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.SystemSettings.Data;

/// <summary>
/// Per-section database context for the SystemSettings section
/// (nobodies-collective/Humans#858): maps only <c>system_settings</c>, with its
/// own <c>__EFMigrationsHistory_SystemSettings</c> table and migrations under
/// <c>Data/Migrations/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <c>HumansDbContext</c> (issue #750): repositories are the
/// only consumers. Configurations are applied explicitly (not by assembly scanning)
/// so this model can never accrete another section's tables.
/// </remarks>
internal sealed class SystemSettingsDbContext(DbContextOptions<SystemSettingsDbContext> options)
    : DbContext(options)
{
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new SystemSettingConfiguration());
    }
}
