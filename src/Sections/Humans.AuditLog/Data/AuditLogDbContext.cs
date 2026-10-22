using Humans.AuditLog.Domain;
using Humans.AuditLog.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.AuditLog.Data;

/// <summary>The <c>audit_log</c> immutability triggers live as raw SQL in the baseline migration, not in this model.</summary>
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
