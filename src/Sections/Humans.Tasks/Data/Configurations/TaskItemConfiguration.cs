using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Tasks.Data.Configurations;

internal sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("task_items");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.OwnerKind).HasMaxLength(50).IsRequired();
        builder.Property(t => t.Title).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(4000);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.BlockedNote).HasMaxLength(500);
        builder.Property(t => t.ApprovalReason).HasMaxLength(200);

        builder.HasIndex(t => new { t.OwnerKind, t.OwnerId });
        builder.HasIndex(t => t.EventId);
    }
}
