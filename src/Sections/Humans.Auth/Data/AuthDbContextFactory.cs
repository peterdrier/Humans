using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Auth.Data;

internal sealed class AuthDbContextFactory : SectionDesignTimeDbContextFactory<AuthDbContext>
{
    protected override AuthDbContext Create(DbContextOptions<AuthDbContext> options) => new(options);
}
