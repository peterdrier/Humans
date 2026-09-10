using Humans.Backdoor.Data.Configurations;
using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Backdoor.Data;

/// <summary>
/// Per-section database context for the Backdoor section: maps only
/// <c>backdoor_api_keys</c>, with its own
/// <c>__EFMigrationsHistory_Backdoor</c> table and migrations under
/// <c>Data/Migrations/</c>. Same database, same connection — the split is a code-side
/// partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: the repository is the only
/// consumer. Key owners are bare Guid references, so the Identity tables stay in
/// <c>UsersDbContext</c> and are deliberately absent here.
/// </remarks>
internal sealed class BackdoorDbContext(DbContextOptions<BackdoorDbContext> options)
    : DbContext(options)
{
    public DbSet<BackdoorApiKey> ApiKeys => Set<BackdoorApiKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new BackdoorApiKeyConfiguration());
    }
}
