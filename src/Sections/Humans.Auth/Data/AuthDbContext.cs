using Humans.Auth.Domain;
using Humans.Auth.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Auth.Data;

internal sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : DbContext(options)
{
    public DbSet<SystemRole> SystemRoles => Set<SystemRole>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new SystemRoleConfiguration());
    }
}
