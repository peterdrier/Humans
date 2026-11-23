using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Tasks.Data.Configurations;

internal sealed class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> builder)
    {
        builder.ToTable("task_dependencies");

        builder.HasKey(d => d.Id);

        builder.HasOne(d => d.TaskItem)
            .WithMany(t => t.Dependencies)
            .HasForeignKey(d => d.TaskItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.DependsOn)
            .WithMany(t => t.Dependents)
            .HasForeignKey(d => d.DependsOnTaskItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.TaskItemId, d.DependsOnTaskItemId }).IsUnique();
        builder.HasIndex(d => d.DependsOnTaskItemId);
    }
}
