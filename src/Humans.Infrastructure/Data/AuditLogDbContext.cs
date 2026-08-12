using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.AuditLog;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the AuditLog horizontal section
/// (nobodies-collective/Humans#858): maps only <c>audit_log</c>, with its own
/// <c>__EFMigrationsHistory_AuditLog</c> table and migrations under
/// <c>Migrations/AuditLog/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// An entry's actor user and resource are bare Guid references, so no other
/// section's tables appear in this model. The <c>audit_log</c> immutability
/// trigger lives as raw SQL in the section baseline, not in this model.
/// </remarks>
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
