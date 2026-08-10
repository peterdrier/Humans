using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Gate;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Gate section
/// (nobodies-collective/Humans#858): maps only <c>gate_scan_events</c>,
/// <c>gate_settings</c> and <c>gate_staff_pins</c>, with its own
/// <c>__EFMigrationsHistory_Gate</c> table and migrations under
/// <c>Migrations/Gate/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// A scan's guest, scanner and override users and a pin's user are bare Guid
/// references, so the Identity tables stay outside this model and are
/// deliberately absent here.
/// </remarks>
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
