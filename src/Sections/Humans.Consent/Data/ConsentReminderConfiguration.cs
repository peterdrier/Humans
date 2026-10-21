using Humans.Consent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Consent.Data;

internal sealed class ConsentReminderConfiguration : IEntityTypeConfiguration<ConsentReminder>
{
    public void Configure(EntityTypeBuilder<ConsentReminder> builder)
    {
        builder.ToTable("consent_reminders");

        builder.HasKey(r => r.UserId);
        builder.Property(r => r.UserId).ValueGeneratedNever();

        builder.Property(r => r.LastSentAt)
            .IsRequired();
    }
}
