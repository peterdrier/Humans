using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Expenses.Data;

internal sealed class ExpensesDbContextFactory : SectionDesignTimeDbContextFactory<ExpensesDbContext>
{
    protected override ExpensesDbContext Create(DbContextOptions<ExpensesDbContext> options) => new(options);
}
