using Humans.Gate.Data.Configurations;
using Humans.Gate.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Gate.Data;

internal sealed class GateDbContext(DbContextOptions<GateDbContext> options)
    : DbContext(options)
{
    public DbSet<GateScanEvent> GateScanEvents => Set<GateScanEvent>();
    public DbSet<GateSettings> GateSettings => Set<GateSettings>();
    public DbSet<GateStaffPin> GateStaffPins => Set<GateStaffPin>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new GateScanEventConfiguration());
        builder.ApplyConfiguration(new GateSettingsConfiguration());
        builder.ApplyConfiguration(new GateStaffPinConfiguration());
    }
}
