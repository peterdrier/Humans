using Humans.AuditLog.Data;
using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Audit.Data;

internal sealed class AuditLogDbContextFactory : SectionDesignTimeDbContextFactory<AuditLogDbContext>
{
    protected override AuditLogDbContext Create(DbContextOptions<AuditLogDbContext> options) => new(options);
}
