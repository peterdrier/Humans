using Humans.Email.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Email.Data;

internal sealed class EmailDailySendCountConfiguration : IEntityTypeConfiguration<EmailDailySendCount>
{
    public void Configure(EntityTypeBuilder<EmailDailySendCount> builder)
    {
        builder.ToTable("email_daily_send_counts");
        builder.HasKey(e => new { e.Date, e.TemplateName });

        builder.Property(e => e.TemplateName).HasMaxLength(100).IsRequired();

        // Dashboard's 90-day window scan.
        builder.HasIndex(e => e.Date);
    }
}
