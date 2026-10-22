using Microsoft.EntityFrameworkCore;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

internal sealed class LegalDbContext(DbContextOptions<LegalDbContext> options)
    : DbContext(options)
{
    public DbSet<LegalDocument> LegalDocuments => Set<LegalDocument>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<ConsentReview> ConsentReviews => Set<ConsentReview>();
    public DbSet<ConsentReminder> ConsentReminders => Set<ConsentReminder>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new LegalDocumentConfiguration());
        builder.ApplyConfiguration(new DocumentVersionConfiguration());
        builder.ApplyConfiguration(new ConsentRecordConfiguration());
        builder.ApplyConfiguration(new ConsentReviewConfiguration());
        builder.ApplyConfiguration(new ConsentReminderConfiguration());
    }
}
