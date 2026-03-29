using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        builder.ToTable("email_outbox_messages");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RecipientEmail).HasMaxLength(320).IsRequired();
        builder.Property(e => e.RecipientName).HasMaxLength(200);
        builder.Property(e => e.Subject).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.HtmlBody).IsRequired();
        builder.Property(e => e.TemplateName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ReplyTo).HasMaxLength(320);
        builder.Property(e => e.ExtraHeaders).HasMaxLength(4000);
        builder.Property(e => e.LastError).HasMaxLength(4000);
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Processor query index
        builder.HasIndex(e => new { e.SentAt, e.RetryCount, e.NextRetryAt, e.PickedUpAt });
        // User email history
        builder.HasIndex(e => e.UserId);
        // Campaign grant tracking
        builder.HasIndex(e => e.CampaignGrantId);

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.CampaignGrant)
            .WithMany(g => g.OutboxMessages)
            .HasForeignKey(e => e.CampaignGrantId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.ShiftSignup)
            .WithMany()
            .HasForeignKey(e => e.ShiftSignupId)
            .OnDelete(DeleteBehavior.SetNull);

        // Dedup: one email of each template type per signup
        builder.HasIndex(e => new { e.ShiftSignupId, e.TemplateName })
            .HasFilter("\"ShiftSignupId\" IS NOT NULL");
    }
}
