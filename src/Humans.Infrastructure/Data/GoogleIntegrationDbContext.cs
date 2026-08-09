using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.GoogleIntegration;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the GoogleIntegration section
/// (nobodies-collective/Humans#858): maps only <c>google_resources</c>,
/// <c>google_sync_outbox</c> and <c>sync_service_settings</c>, with its own
/// <c>__EFMigrationsHistory_GoogleIntegration</c> table and migrations under
/// <c>Migrations/GoogleIntegration/</c>. Same database, same connection — the
/// split is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// The team a resource is provisioned for is a bare Guid, so the Teams tables
/// stay in <see cref="HumansDbContext"/> and are deliberately absent here.
/// <c>SyncServiceSettings</c> carries a model-level <c>HasData</c> singleton, so
/// the generated baseline re-emits the seed the old chain inserted.
/// </remarks>
internal sealed class GoogleIntegrationDbContext(DbContextOptions<GoogleIntegrationDbContext> options)
    : DbContext(options)
{
    public DbSet<GoogleResource> GoogleResources => Set<GoogleResource>();
    public DbSet<GoogleSyncOutboxEvent> GoogleSyncOutboxEvents => Set<GoogleSyncOutboxEvent>();
    public DbSet<SyncServiceSettings> SyncServiceSettings => Set<SyncServiceSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new GoogleResourceConfiguration());
        builder.ApplyConfiguration(new GoogleSyncOutboxEventConfiguration());
        builder.ApplyConfiguration(new SyncServiceSettingsConfiguration());
    }
}
