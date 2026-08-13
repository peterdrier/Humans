using Microsoft.EntityFrameworkCore;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

/// <summary>
/// Per-section database context for the Legal section
/// (nobodies-collective/Humans#858): maps only <c>legal_documents</c>,
/// <c>document_versions</c> and <c>consent_records</c>, with its own
/// <c>__EFMigrationsHistory_Legal</c> table and migrations under
/// <c>Migrations/Legal/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// A consent record's user and a legal document's team are bare Guid
/// references, so the Identity and Teams tables stay outside this model and
/// are deliberately absent here. The <c>consent_records</c> immutability
/// trigger lives as raw SQL in the section baseline, not in this model.
/// </remarks>
internal sealed class LegalDbContext(DbContextOptions<LegalDbContext> options)
    : DbContext(options)
{
    public DbSet<LegalDocument> LegalDocuments => Set<LegalDocument>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new LegalDocumentConfiguration());
        builder.ApplyConfiguration(new DocumentVersionConfiguration());
        builder.ApplyConfiguration(new ConsentRecordConfiguration());
    }
}
