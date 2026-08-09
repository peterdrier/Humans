using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Notifications section
/// (nobodies-collective/Humans#858): maps only <c>notifications</c> and
/// <c>notification_recipients</c>, with its own
/// <c>__EFMigrationsHistory_Notifications</c> table and migrations under
/// <c>Migrations/Notifications/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Recipients are bare Guid user references, so the Identity tables stay in
/// <see cref="HumansDbContext"/> and are deliberately absent here.
/// </remarks>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new NotificationConfiguration());
        builder.ApplyConfiguration(new NotificationRecipientConfiguration());
    }
}
