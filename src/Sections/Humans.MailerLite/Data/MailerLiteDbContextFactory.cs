using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.MailerLite.Data;

internal sealed class MailerLiteDbContextFactory : SectionDesignTimeDbContextFactory<MailerLiteDbContext>
{
    protected override MailerLiteDbContext Create(DbContextOptions<MailerLiteDbContext> options) => new(options);
}
