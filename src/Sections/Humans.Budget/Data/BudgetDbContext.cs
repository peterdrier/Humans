using Humans.Budget.Domain;
using Humans.Budget.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Budget.Data;

internal sealed class BudgetDbContext(DbContextOptions<BudgetDbContext> options)
    : DbContext(options)
{
    public DbSet<BudgetYear> BudgetYears => Set<BudgetYear>();
    public DbSet<BudgetGroup> BudgetGroups => Set<BudgetGroup>();
    public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
    public DbSet<BudgetLineItem> BudgetLineItems => Set<BudgetLineItem>();
    public DbSet<BudgetAuditLog> BudgetAuditLogs => Set<BudgetAuditLog>();
    public DbSet<TicketingProjection> TicketingProjections => Set<TicketingProjection>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new BudgetYearConfiguration());
        builder.ApplyConfiguration(new BudgetGroupConfiguration());
        builder.ApplyConfiguration(new BudgetCategoryConfiguration());
        builder.ApplyConfiguration(new BudgetLineItemConfiguration());
        builder.ApplyConfiguration(new BudgetAuditLogConfiguration());
        builder.ApplyConfiguration(new TicketingProjectionConfiguration());
    }
}
