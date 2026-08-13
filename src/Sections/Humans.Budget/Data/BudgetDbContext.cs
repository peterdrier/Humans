using Humans.Budget.Domain;
using Humans.Budget.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Budget.Data;

/// <summary>
/// Per-section database context for the Budget section
/// (nobodies-collective/Humans#858): maps only <c>budget_years</c>,
/// <c>budget_groups</c>, <c>budget_categories</c>, <c>budget_line_items</c>,
/// <c>budget_audit_logs</c> and <c>ticketing_projections</c>, with its own
/// <c>__EFMigrationsHistory_Budget</c> table and migrations under
/// <c>Migrations/Budget/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Owning teams and acting users are bare Guid references, so the Teams and
/// Identity tables stay in <see cref="UsersDbContext"/> and are deliberately
/// absent here.
/// </remarks>
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
