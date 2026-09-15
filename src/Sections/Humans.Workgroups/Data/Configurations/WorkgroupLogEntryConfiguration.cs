using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupLogEntryConfiguration : IEntityTypeConfiguration<WorkgroupLogEntry>
{
    public void Configure(EntityTypeBuilder<WorkgroupLogEntry> b)
    {
        b.ToTable("workgroup_log_entries");
        b.HasKey(e => e.Id);

        b.Property(e => e.WorkgroupId).IsRequired();
        b.Property(e => e.Kind).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(e => e.OccurredOn).IsRequired();
        b.Property(e => e.Title).HasMaxLength(200);
        b.Property(e => e.Body).HasMaxLength(16000);
        // AuthorUserId and SurveyId are bare cross-section references: no FK, no navigation.
        b.Property(e => e.CreatedAt).IsRequired();
        b.Property(e => e.UpdatedAt).IsRequired();

        b.HasOne(e => e.Workgroup)
            .WithMany(w => w.LogEntries)
            .HasForeignKey(e => e.WorkgroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a document leaves its log entries standing, unlinked: the history
        // of what the group did is not the document's to take with it.
        b.HasOne(e => e.Document)
            .WithMany()
            .HasForeignKey(e => e.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(e => new { e.WorkgroupId, e.OccurredOn });
        b.HasIndex(e => e.AuthorUserId).HasFilter("\"AuthorUserId\" IS NOT NULL");
    }
}
