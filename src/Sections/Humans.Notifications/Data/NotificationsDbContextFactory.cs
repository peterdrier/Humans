using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Notifications.Data;

internal sealed class NotificationsDbContextFactory : SectionDesignTimeDbContextFactory<NotificationsDbContext>
{
    protected override NotificationsDbContext Create(DbContextOptions<NotificationsDbContext> options) => new(options);
}
