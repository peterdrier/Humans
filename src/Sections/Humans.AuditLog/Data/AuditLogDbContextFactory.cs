using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.AuditLog.Data;

internal sealed class AuditLogDbContextFactory : SectionDesignTimeDbContextFactory<AuditLogDbContext>
{
    protected override AuditLogDbContext Create(DbContextOptions<AuditLogDbContext> options) => new(options);
}
