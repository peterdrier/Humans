using Humans.Email.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Email.Data;

internal sealed class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        builder.ToTable("email_outbox_messages");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RecipientEmail).HasMaxLength(320).IsRequired();
        builder.Property(e => e.RecipientName).HasMaxLength(256);
        builder.Property(e => e.Subject).HasMaxLength(EmailOutboxMessage.SubjectMaxLength).IsRequired();
        builder.Property(e => e.HtmlBody).IsRequired();
        builder.Property(e => e.TemplateName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ReplyTo).HasMaxLength(320);
        builder.Property(e => e.ExtraHeaders).HasMaxLength(4000);
        builder.Property(e => e.LastError).HasMaxLength(4000);
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Processor batch query
        builder.HasIndex(e => new { e.SentAt, e.RetryCount, e.NextRetryAt, e.PickedUpAt });
        // User email history
        builder.HasIndex(e => e.UserId);
        // Kept with its unwritten column until the drop is approved (nobodies-collective/Humans#1243).
        builder.HasIndex(e => e.CampaignGrantId);

        // UserId and CampaignGrantId have no FK: a stale id is an accepted orphan in this append-only,
        // age-pruned log.
    }
}
