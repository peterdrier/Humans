using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Data;

/// <summary>Framework-owned tables only. Adding a table here is Peter's call.</summary>
internal sealed class SystemDbContext(DbContextOptions<SystemDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
