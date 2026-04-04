using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class TicketOrderConfiguration : IEntityTypeConfiguration<TicketOrder>
{
    public void Configure(EntityTypeBuilder<TicketOrder> builder)
    {
        builder.ToTable("ticket_orders");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.VendorOrderId)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(o => o.VendorOrderId)
            .IsUnique();

        builder.Property(o => o.BuyerName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(o => o.BuyerEmail)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(o => o.TotalAmount)
            .HasPrecision(10, 2);

        builder.Property(o => o.Currency)
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(o => o.DiscountCode)
            .HasMaxLength(100);

        builder.Property(o => o.PaymentStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(o => o.VendorEventId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(o => o.VendorDashboardUrl)
            .HasMaxLength(500);

        builder.Property(o => o.PurchasedAt)
            .IsRequired();

        builder.Property(o => o.SyncedAt)
            .IsRequired();

        builder.HasOne(o => o.MatchedUser)
            .WithMany()
            .HasForeignKey(o => o.MatchedUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(o => o.Attendees)
            .WithOne(a => a.TicketOrder)
            .HasForeignKey(a => a.TicketOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(o => o.StripePaymentIntentId)
            .HasMaxLength(100);

        builder.Property(o => o.PaymentMethod)
            .HasMaxLength(50);

        builder.Property(o => o.PaymentMethodDetail)
            .HasMaxLength(50);

        builder.Property(o => o.StripeFee)
            .HasPrecision(10, 2);

        builder.Property(o => o.ApplicationFee)
            .HasPrecision(10, 2);

        builder.Property(o => o.DiscountAmount)
            .HasPrecision(10, 2);

        builder.Property(o => o.DonationAmount)
            .IsRequired()
            .HasPrecision(10, 2);

        builder.Property(o => o.VatAmount)
            .IsRequired()
            .HasPrecision(10, 2);

        builder.HasIndex(o => o.BuyerEmail);
        builder.HasIndex(o => o.PurchasedAt);
        builder.HasIndex(o => o.MatchedUserId);
        builder.HasIndex(o => o.PaymentMethod);
    }
}
