using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Finance.Data;

internal sealed class FinanceDbContextFactory : SectionDesignTimeDbContextFactory<FinanceDbContext>
{
    protected override FinanceDbContext Create(DbContextOptions<FinanceDbContext> options) => new(options);
}
