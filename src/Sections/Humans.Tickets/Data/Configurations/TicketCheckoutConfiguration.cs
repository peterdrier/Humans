using Humans.Tickets.Domain.Checkout;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Tickets.Data.Configurations;

internal sealed class TicketingSettingsConfiguration : IEntityTypeConfiguration<TicketingSettings>
{
    public void Configure(EntityTypeBuilder<TicketingSettings> builder)
    {
        builder.ToTable("ticket_sales_events");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.EventSettingsId).IsUnique();
        // Columns keep their names from before the glossary: renaming them is a migration for no gain.
        builder.Property(x => x.PlatformKey).HasColumnName("BackendKey").IsRequired().HasMaxLength(32);
        builder.Property(x => x.PlatformEventRef).HasColumnName("BackendEventRef").HasMaxLength(128);
        builder.Property(x => x.VipTotalsMinor).HasColumnName("SupporterTotalsMinor");
        builder.Property(x => x.TermsFolder).HasMaxLength(200);
        builder.Property(x => x.CancelReason).HasMaxLength(500);
        builder.Property(x => x.Mode).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.TicketVatRate).HasPrecision(5, 4);
        builder.OwnsMany(x => x.Waves, w =>
        {
            w.ToJson("waves");
            w.Property(p => p.Name).HasMaxLength(64);
        });
        builder.OwnsMany(x => x.PriceRules, r =>
        {
            r.ToJson("price_rules");
            // Discount is stored under its old name, Credit.
            r.Property(p => p.Kind).HasConversion(
                k => k == TicketPriceRuleKind.Discount ? "Credit" : k.ToString(),
                v => v == "Credit" ? TicketPriceRuleKind.Discount : Enum.Parse<TicketPriceRuleKind>(v));
        });
        // Replaced by ChildTypes; unread, kept until a drop of its own is approved (destructive-drops-need-approval).
        builder.Property<long?>("ChildPriceMinor");
        builder.Property<int?>("ChildMaxAge");
        builder.OwnsMany(x => x.ChildTypes, c =>
        {
            c.ToJson("child_types");
            c.Property(p => p.Name).HasMaxLength(64);
        });
        builder.OwnsOne(x => x.Switches, s =>
        {
            s.ToJson("switches");
            s.Property(p => p.Resale).HasConversion<string>();
            s.Ignore(p => p.CompanionTicketsOn);
        });
    }
}

internal sealed class TicketCheckoutOrderConfiguration : IEntityTypeConfiguration<TicketCheckoutOrder>
{
    public void Configure(EntityTypeBuilder<TicketCheckoutOrder> builder)
    {
        builder.ToTable("ticket_checkout_orders");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.TicketingSettingsId);
        builder.HasIndex(x => x.BuyerUserId);
        builder.HasIndex(x => new { x.PlatformKey, x.PlatformOrderId }).IsUnique();
        builder.Property(x => x.TicketingSettingsId).HasColumnName("SalesEventId");
        builder.Property(x => x.ChargedBackMinor).HasColumnName("DisputedMinor");
        builder.Property(x => x.BuyerName).IsRequired().HasMaxLength(513);
        builder.Property(x => x.BuyerEmail).IsRequired().HasMaxLength(320);
        builder.Property(x => x.PlatformKey).HasColumnName("BackendKey").IsRequired().HasMaxLength(32);
        builder.Property(x => x.PlatformOrderId).HasColumnName("BackendOrderId").HasMaxLength(256);
        builder.Property(x => x.PaymentUrl).HasMaxLength(2048);
        builder.Property(x => x.AdminUrl).HasMaxLength(2048);
        builder.Property(x => x.State).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        builder.Property(x => x.Culture).IsRequired().HasMaxLength(16);
        builder.Property(x => x.PaymentMethod).HasMaxLength(64);
        builder.Property(x => x.TermsFolder).HasMaxLength(200);
        builder.Property(x => x.TermsVersion).HasMaxLength(40);
        builder.Ignore(x => x.TicketTotalMinor);
        builder.OwnsMany(x => x.Lines, l =>
        {
            l.ToJson("lines");
            l.Property(p => p.Kind).HasConversion<string>();
            l.Property(p => p.HolderKind).HasConversion<string>();
            l.Property(p => p.HolderName).HasJsonPropertyName("AttendeeName");
            l.Property(p => p.HolderEmail).HasJsonPropertyName("AttendeeEmail");
            l.Property(p => p.HolderUserId).HasJsonPropertyName("AttendeeUserId");
            l.Property(p => p.DiscountsMinor).HasJsonPropertyName("CreditsMinor");
        });
    }
}

internal sealed class TicketCheckoutRefundConfiguration : IEntityTypeConfiguration<TicketCheckoutRefund>
{
    public void Configure(EntityTypeBuilder<TicketCheckoutRefund> builder)
    {
        builder.ToTable("ticket_checkout_refunds");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.OrderId);
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(500);
        builder.Property(x => x.State).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.PlatformRefundId).HasColumnName("BackendRefundId").HasMaxLength(256);
        builder.Property(x => x.FailureReason).HasMaxLength(1000);
        builder.Property(x => x.ManualRefundUrl).HasMaxLength(2048);
    }
}

internal sealed class TicketCheckoutOrderEventConfiguration : IEntityTypeConfiguration<TicketCheckoutOrderEvent>
{
    public void Configure(EntityTypeBuilder<TicketCheckoutOrderEvent> builder)
    {
        builder.ToTable("ticket_checkout_order_events");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.OrderId);
        // PlatformRefused is stored under its old name, BackendRefused: the timeline is never rewritten.
        builder.Property(x => x.Kind).IsRequired().HasConversion(
            k => k == TicketCheckoutOrderEventKind.PlatformRefused ? "BackendRefused" : k.ToString(),
            v => v == "BackendRefused" ? TicketCheckoutOrderEventKind.PlatformRefused : Enum.Parse<TicketCheckoutOrderEventKind>(v))
            .HasMaxLength(32);
        builder.Property(x => x.Detail).IsRequired().HasMaxLength(2000);
    }
}

internal sealed class TicketCheckoutTicketConfiguration : IEntityTypeConfiguration<TicketCheckoutTicket>
{
    public void Configure(EntityTypeBuilder<TicketCheckoutTicket> builder)
    {
        builder.ToTable("ticket_checkout_tickets");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.Barcode).IsUnique();
        builder.HasIndex(x => new { x.OrderId, x.LineIndex }).IsUnique();
        builder.HasIndex(x => x.TicketingSettingsId);
        builder.Property(x => x.TicketingSettingsId).HasColumnName("SalesEventId");
        builder.HasIndex(x => x.HolderUserId);
        builder.HasIndex(x => x.ParentTicketId);
        builder.Property(x => x.Kind).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.State).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.HolderName).IsRequired().HasMaxLength(513);
        builder.Property(x => x.HolderEmail).HasMaxLength(320);
        builder.Property(x => x.Barcode).IsRequired().HasMaxLength(32);
        builder.Property(x => x.WaveName).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ClassLabel).HasMaxLength(128);
        builder.Property(x => x.VoidReason).HasMaxLength(500);
        builder.Property(x => x.VoidRequestedReason).HasMaxLength(500);
        builder.Property(x => x.DiscountsMinor).HasColumnName("CreditsMinor");
    }
}

internal sealed class TicketResaleOfferConfiguration : IEntityTypeConfiguration<TicketResaleOffer>
{
    public void Configure(EntityTypeBuilder<TicketResaleOffer> builder)
    {
        builder.ToTable("ticket_resale_offers");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.TicketingSettingsId);
        builder.Property(x => x.TicketingSettingsId).HasColumnName("SalesEventId");
        builder.HasIndex(x => x.TicketId);
        builder.Property(x => x.State).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.OwnsMany(x => x.Invites, i => i.ToJson("directed_invites"));
    }
}

internal sealed class TicketCompanionRequestConfiguration : IEntityTypeConfiguration<TicketCompanionRequest>
{
    public void Configure(EntityTypeBuilder<TicketCompanionRequest> builder)
    {
        builder.ToTable("ticket_companion_requests");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.TicketingSettingsId);
        builder.Property(x => x.TicketingSettingsId).HasColumnName("SalesEventId");
        builder.HasIndex(x => x.TicketId);
        builder.Property(x => x.State).IsRequired().HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Note).IsRequired().HasMaxLength(500);
        builder.Property(x => x.DecisionNote).HasMaxLength(500);
    }
}

