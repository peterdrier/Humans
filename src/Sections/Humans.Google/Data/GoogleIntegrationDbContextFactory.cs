using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Google.Data;

internal sealed class GoogleIntegrationDbContextFactory : SectionDesignTimeDbContextFactory<GoogleIntegrationDbContext>
{
    protected override GoogleIntegrationDbContext Create(DbContextOptions<GoogleIntegrationDbContext> options) => new(options);
}
