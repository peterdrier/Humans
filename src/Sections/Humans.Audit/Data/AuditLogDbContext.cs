using Humans.Audit.Domain;
using Humans.Audit.Data.Configurations;
using Microsoft.EntityFrameworkCore;

// Keeps the pre-rename name and namespace: the merged migrations name this type, and the history table
// (__EFMigrationsHistory_AuditLog) is derived from it. Both are frozen until the migrations are rebaselined.
namespace Humans.AuditLog.Data;

/// <summary>The <c>audit_log_entries</c> immutability triggers live as raw SQL in the baseline migration, not in this model.</summary>
internal sealed class AuditLogDbContext(DbContextOptions<AuditLogDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new AuditLogEntryConfiguration());
    }
}
