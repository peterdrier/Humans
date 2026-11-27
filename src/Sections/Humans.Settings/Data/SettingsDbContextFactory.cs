using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Settings.Data;

internal sealed class SettingsDbContextFactory : SectionDesignTimeDbContextFactory<SettingsDbContext>
{
    protected override SettingsDbContext Create(DbContextOptions<SettingsDbContext> options) => new(options);
}
