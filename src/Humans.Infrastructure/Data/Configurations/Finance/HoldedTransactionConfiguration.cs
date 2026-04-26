using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedTransactionConfiguration : IEntityTypeConfiguration<HoldedTransaction>
{
    public void Configure(EntityTypeBuilder<HoldedTransaction> builder)
    {
        builder.ToTable("holded_transactions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.HoldedDocId).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.HoldedDocId).IsUnique();

        builder.Property(t => t.HoldedDocNumber).HasMaxLength(64).IsRequired();
        builder.Property(t => t.ContactName).HasMaxLength(512).IsRequired();

        builder.Property(t => t.Date).IsRequired();
        builder.Property(t => t.AccountingDate);
        builder.Property(t => t.DueDate);

        builder.Property(t => t.Subtotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.Tax).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.Total).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.PaymentsTotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.PaymentsPending).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.PaymentsRefunds).HasColumnType("numeric(18,2)").IsRequired();

        builder.Property(t => t.Currency).HasMaxLength(3).IsRequired();
        builder.Property(t => t.ApprovedAt);

        // Tags: store as JSON string, expose as IReadOnlyList<string>.
        var tagsConverter = new ValueConverter<IReadOnlyList<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        builder.Property(t => t.Tags)
            .HasConversion(tagsConverter)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(t => t.RawPayload).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.SourceIncomingDocId).HasMaxLength(64);

        builder.Property(t => t.BudgetCategoryId);
        builder.HasIndex(t => t.BudgetCategoryId);

        builder.Property(t => t.MatchStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.HasIndex(t => t.MatchStatus);

        builder.Property(t => t.LastSyncedAt).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        // BudgetCategoryId is FK-only, no navigation property.
        // OnDelete restrict to refuse deleting a category that has matched transactions.
        builder.HasOne<BudgetCategory>()
            .WithMany()
            .HasForeignKey(t => t.BudgetCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
