using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Tasks.Data.Configurations;

internal sealed class TaskAssignmentConfiguration : IEntityTypeConfiguration<TaskAssignment>
{
    public void Configure(EntityTypeBuilder<TaskAssignment> builder)
    {
        builder.ToTable("task_assignments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.State).HasConversion<string>().HasMaxLength(50);

        builder.HasOne(a => a.TaskItem)
            .WithMany(t => t.Assignments)
            .HasForeignKey(a => a.TaskItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.TaskItemId, a.UserId }).IsUnique();
        builder.HasIndex(a => a.UserId);
    }
}
