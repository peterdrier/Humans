using Humans.Finance.Domain;
using Humans.Finance.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Finance.Data;

internal sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options)
    : DbContext(options)
{
    public DbSet<HoldedExpenseDoc> HoldedExpenseDocs => Set<HoldedExpenseDoc>();
    public DbSet<HoldedCategoryMap> HoldedCategoryMap => Set<HoldedCategoryMap>();
    public DbSet<HoldedCreditorContact> HoldedCreditorContacts => Set<HoldedCreditorContact>();
    public DbSet<HoldedDocSyncState> HoldedDocSyncStates => Set<HoldedDocSyncState>();
    public DbSet<HoldedManagedAccount> HoldedManagedAccounts => Set<HoldedManagedAccount>();
    public DbSet<SepaPayoutFile> SepaPayoutFiles => Set<SepaPayoutFile>();
    public DbSet<SepaPayoutTransfer> SepaPayoutTransfers => Set<SepaPayoutTransfer>();
    public DbSet<PaymentDetails> PaymentDetails => Set<PaymentDetails>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new HoldedExpenseDocConfiguration());
        builder.ApplyConfiguration(new HoldedCategoryMapConfiguration());
        builder.ApplyConfiguration(new HoldedCreditorContactConfiguration());
        builder.ApplyConfiguration(new HoldedDocSyncStateConfiguration());
        builder.ApplyConfiguration(new HoldedManagedAccountConfiguration());
        builder.ApplyConfiguration(new SepaPayoutFileConfiguration());
        builder.ApplyConfiguration(new SepaPayoutTransferConfiguration());
        builder.ApplyConfiguration(new PaymentDetailsConfiguration());
    }
}
