using Humans.Roles.Data.Configurations;
using Humans.Roles.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Roles.Data;

internal sealed class RolesDbContext(DbContextOptions<RolesDbContext> options) : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new RoleConfiguration());
        builder.ApplyConfiguration(new RoleAssignmentConfiguration());
    }
}
