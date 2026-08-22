using Microsoft.EntityFrameworkCore;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;

/// <summary>
/// Per-section database context for the Expenses section
/// (nobodies-collective/Humans#858): maps only <c>expense_reports</c>,
/// <c>expense_lines</c>, <c>expense_attachments</c>,
/// <c>holded_expense_outbox_events</c>, <c>vendor_commitments</c>,
/// <c>vendor_commitment_payments</c> and
/// <c>vendor_commitment_match_candidates</c>, with its own
/// <c>__EFMigrationsHistory_Expenses</c> table and migrations under
/// <c>Migrations/Expenses/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
internal sealed class ExpensesDbContext(DbContextOptions<ExpensesDbContext> options)
    : DbContext(options)
{
    public DbSet<ExpenseReport> ExpenseReports => Set<ExpenseReport>();
    public DbSet<ExpenseLine> ExpenseLines => Set<ExpenseLine>();
    public DbSet<ExpenseAttachment> ExpenseAttachments => Set<ExpenseAttachment>();
    public DbSet<HoldedExpenseOutboxEvent> HoldedExpenseOutboxEvents => Set<HoldedExpenseOutboxEvent>();
    public DbSet<VendorCommitment> VendorCommitments => Set<VendorCommitment>();
    public DbSet<VendorCommitmentPayment> VendorCommitmentPayments => Set<VendorCommitmentPayment>();
    public DbSet<VendorCommitmentMatchCandidate> VendorCommitmentMatchCandidates
        => Set<VendorCommitmentMatchCandidate>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ExpenseReportConfiguration());
        builder.ApplyConfiguration(new ExpenseLineConfiguration());
        builder.ApplyConfiguration(new ExpenseAttachmentConfiguration());
        builder.ApplyConfiguration(new HoldedExpenseOutboxEventConfiguration());
        builder.ApplyConfiguration(new VendorCommitmentConfiguration());
        builder.ApplyConfiguration(new VendorCommitmentPaymentConfiguration());
        builder.ApplyConfiguration(new VendorCommitmentMatchCandidateConfiguration());
    }
}
