using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Users.Data;

internal sealed class UsersDbContextFactory : SectionDesignTimeDbContextFactory<UsersDbContext>
{
    protected override UsersDbContext Create(DbContextOptions<UsersDbContext> options) => new(options);
}
