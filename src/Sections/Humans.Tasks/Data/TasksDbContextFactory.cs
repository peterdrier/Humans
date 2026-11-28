using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Tasks.Data;

internal sealed class TasksDbContextFactory : SectionDesignTimeDbContextFactory<TasksDbContext>
{
    protected override TasksDbContext Create(DbContextOptions<TasksDbContext> options) => new(options);
}
