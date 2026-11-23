using Humans.Tasks.Data.Configurations;
using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Tasks.Data;

internal sealed class TasksDbContext(DbContextOptions<TasksDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<TaskAssignment> TaskAssignments => Set<TaskAssignment>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new TaskItemConfiguration());
        builder.ApplyConfiguration(new TaskAssignmentConfiguration());
        builder.ApplyConfiguration(new TaskDependencyConfiguration());
    }
}
