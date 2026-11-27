using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Budget.Data;

internal sealed class BudgetDbContextFactory : SectionDesignTimeDbContextFactory<BudgetDbContext>
{
    protected override BudgetDbContext Create(DbContextOptions<BudgetDbContext> options) => new(options);
}
