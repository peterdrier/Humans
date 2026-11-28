using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Email.Data;

internal sealed class EmailDbContextFactory : SectionDesignTimeDbContextFactory<EmailDbContext>
{
    protected override EmailDbContext Create(DbContextOptions<EmailDbContext> options) => new(options);
}
