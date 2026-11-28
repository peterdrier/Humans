using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Consent.Data;

internal sealed class LegalDbContextFactory : SectionDesignTimeDbContextFactory<LegalDbContext>
{
    protected override LegalDbContext Create(DbContextOptions<LegalDbContext> options) => new(options);
}
