using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data.Configurations;
using Humans.GoogleIntegration.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.GoogleIntegration.Data;

internal sealed class GoogleIntegrationDbContext(DbContextOptions<GoogleIntegrationDbContext> options)
    : DbContext(options)
{
    public DbSet<GoogleResource> GoogleResources => Set<GoogleResource>();
    public DbSet<GoogleSyncOutboxEvent> GoogleSyncOutboxEvents => Set<GoogleSyncOutboxEvent>();
    public DbSet<SyncServiceSettings> SyncServiceSettings => Set<SyncServiceSettings>();
    public DbSet<GoogleSyncLogEntry> GoogleSyncLog => Set<GoogleSyncLogEntry>();
    public DbSet<WorkspaceIdentityRecord> WorkspaceIdentities => Set<WorkspaceIdentityRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new GoogleResourceConfiguration());
        builder.ApplyConfiguration(new GoogleSyncOutboxEventConfiguration());
        builder.ApplyConfiguration(new SyncServiceSettingsConfiguration());
        builder.ApplyConfiguration(new GoogleSyncLogEntryConfiguration());
        builder.ApplyConfiguration(new WorkspaceIdentityRecordConfiguration());
    }
}
