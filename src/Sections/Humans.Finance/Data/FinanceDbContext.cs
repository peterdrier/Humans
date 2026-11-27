using Humans.Finance.Domain;
using Humans.Finance.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Finance.Data;

internal sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options)
    : DbContext(options)
{
    public DbSet<HoldedCategoryMap> HoldedCategoryMap => Set<HoldedCategoryMap>();
    public DbSet<HoldedCreditorContact> HoldedCreditorContacts => Set<HoldedCreditorContact>();
    public DbSet<HoldedManagedAccount> HoldedManagedAccounts => Set<HoldedManagedAccount>();
    public DbSet<SepaPayoutFile> SepaPayoutFiles => Set<SepaPayoutFile>();
    public DbSet<SepaPayoutTransfer> SepaPayoutTransfers => Set<SepaPayoutTransfer>();
    public DbSet<PaymentDetails> PaymentDetails => Set<PaymentDetails>();
    public DbSet<StripeLedgerLink> StripeLedgerLinks => Set<StripeLedgerLink>();
    public DbSet<FinanceSettings> Settings => Set<FinanceSettings>();
    public DbSet<PleoBookingClaim> PleoBookingClaims => Set<PleoBookingClaim>();
    public DbSet<PleoReceiptUpload> PleoReceiptUploads => Set<PleoReceiptUpload>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new HoldedCategoryMapConfiguration());
        builder.ApplyConfiguration(new HoldedCreditorContactConfiguration());
        builder.ApplyConfiguration(new HoldedManagedAccountConfiguration());
        builder.ApplyConfiguration(new SepaPayoutFileConfiguration());
        builder.ApplyConfiguration(new SepaPayoutTransferConfiguration());
        builder.ApplyConfiguration(new PaymentDetailsConfiguration());
        builder.ApplyConfiguration(new StripeLedgerLinkConfiguration());
        builder.ApplyConfiguration(new FinanceSettingsConfiguration());
        builder.ApplyConfiguration(new PleoBookingClaimConfiguration());
        builder.ApplyConfiguration(new PleoReceiptUploadConfiguration());
    }
}
