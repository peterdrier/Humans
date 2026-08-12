using Humans.Email.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Email.Data;

/// <summary>
/// Per-section database context for the Email section
/// (nobodies-collective/Humans#858): maps only <c>email_outbox_messages</c>,
/// with its own <c>__EFMigrationsHistory_Email</c> table and migrations under
/// <c>Migrations/Email/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// The outbox rows carry bare Guid references to the campaign grants, shift
/// signups and users they were raised for; those sections' tables stay in
/// <see cref="HumansDbContext"/> and are deliberately absent here.
/// </remarks>
internal sealed class EmailDbContext(DbContextOptions<EmailDbContext> options)
    : DbContext(options)
{
    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new EmailOutboxMessageConfiguration());
    }
}
