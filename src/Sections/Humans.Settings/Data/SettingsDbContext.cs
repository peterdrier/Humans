using Humans.Settings.Data.Configurations;
using Humans.Settings.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Settings.Data;

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
