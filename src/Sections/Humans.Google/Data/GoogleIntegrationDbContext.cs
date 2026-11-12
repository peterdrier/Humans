using Humans.Google.Contracts;
using Humans.Google.Data.Configurations;
using Humans.Google.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Google.Data;

internal sealed class GoogleIntegrationDbContext(DbContextOptions<GoogleIntegrationDbContext> options)
    : DbContext(options)
{
    public DbSet<TeamGoogleResource> GoogleResources => Set<TeamGoogleResource>();
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
