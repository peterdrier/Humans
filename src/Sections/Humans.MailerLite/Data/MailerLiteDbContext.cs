using Humans.MailerLite.Data.Configurations;
using Humans.MailerLite.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.MailerLite.Data;

/// <summary>
/// Per-section database context for MailerLite: maps only <c>mailerlite_sync_states</c>, with
/// its own <c>__EFMigrationsHistory_MailerLite</c> table and migrations under
/// <c>Data/Migrations/</c>. Same database, same connection — the split is a code-side partition
/// of the EF model.
/// </summary>
/// <remarks>
/// MailerLite remains the system of record for subscriber state; the one thing Humans owns is
/// when each audience last synced and what happened (nobodies-collective/Humans#1082). Internal
/// and sealed like <c>HoldedDbContext</c> — the repository is the only consumer, and
/// configurations are applied explicitly so this model can never accrete another section's tables.
/// </remarks>
internal sealed class MailerLiteDbContext(DbContextOptions<MailerLiteDbContext> options)
    : DbContext(options)
{
    public DbSet<MailerLiteSyncState> MailerLiteSyncStates => Set<MailerLiteSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new MailerLiteSyncStateConfiguration());
    }
}
