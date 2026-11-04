using Humans.Pleo.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Pleo.Data.Configurations;

internal sealed class PleoEntryConfiguration : IEntityTypeConfiguration<PleoEntry>
{
    public void Configure(EntityTypeBuilder<PleoEntry> b)
    {
        b.ToTable("pleo_entries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.EmployeeId).HasMaxLength(64);
        b.Property(x => x.TeamId).HasMaxLength(64);
        b.Property(x => x.CategoryId).HasMaxLength(64);
        b.Property(x => x.Family).HasMaxLength(64);
        b.Property(x => x.SubFamily).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(32);
        b.Property(x => x.ExportStatus).HasMaxLength(32);
        b.Property(x => x.ReviewStatus).HasMaxLength(32);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.AmountEur).HasColumnType("decimal(14,2)");
        b.Property(x => x.MerchantName).HasMaxLength(256);
        b.Property(x => x.MerchantCategoryCode).HasMaxLength(8);
        b.Property(x => x.MerchantCountry).HasMaxLength(8);
        b.Property(x => x.Note).HasMaxLength(2000);
        b.Property(x => x.Raw).HasColumnType("jsonb");
    }
}

internal sealed class PleoEmployeeConfiguration : IEntityTypeConfiguration<PleoEmployee>
{
    public void Configure(EntityTypeBuilder<PleoEmployee> b)
    {
        b.ToTable("pleo_employees");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.FirstName).HasMaxLength(128);
        b.Property(x => x.LastName).HasMaxLength(128);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Ignore(x => x.DisplayName);
    }
}

internal sealed class PleoAccountConfiguration : IEntityTypeConfiguration<PleoAccount>
{
    public void Configure(EntityTypeBuilder<PleoAccount> b)
    {
        b.ToTable("pleo_accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.ExternalId).HasMaxLength(64);
    }
}

internal sealed class PleoBalanceConfiguration : IEntityTypeConfiguration<PleoBalance>
{
    public void Configure(EntityTypeBuilder<PleoBalance> b)
    {
        b.ToTable("pleo_balance");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Currency).HasMaxLength(3);
    }
}

internal sealed class PleoSyncStateConfiguration : IEntityTypeConfiguration<PleoSyncState>
{
    public void Configure(EntityTypeBuilder<PleoSyncState> b)
    {
        b.ToTable("pleo_sync_states");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
