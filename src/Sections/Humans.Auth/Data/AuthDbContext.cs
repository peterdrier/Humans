using Humans.Auth.Domain;
using Humans.Auth.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Auth.Data;

/// <summary>
/// Per-section database context for the Auth section
/// (nobodies-collective/Humans#858): maps only <c>role_assignments</c>, with its
/// own <c>__EFMigrationsHistory_Auth</c> table and migrations under
/// <c>Migrations/Auth/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Auth is a horizontal section; the Identity tables it authenticates against
/// stay in <see cref="UsersDbContext"/> and are deliberately absent here —
/// <c>RoleAssignment.UserId</c> is a bare Guid.
/// </remarks>
internal sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : DbContext(options)
{
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new RoleAssignmentConfiguration());
    }
}
