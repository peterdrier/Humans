using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Notifications.Domain;

namespace Humans.Notifications.Data.Configurations;

internal sealed class NotificationRecipientConfiguration : IEntityTypeConfiguration<NotificationRecipient>
{
    public void Configure(EntityTypeBuilder<NotificationRecipient> builder)
    {
        builder.ToTable("notification_recipients");

        builder.HasKey(r => new { r.NotificationId, r.UserId });

        // Index for badge count query: find unread notifications for a user
        builder.HasIndex(r => r.UserId)
            .HasDatabaseName("IX_NotificationRecipient_UserId");
    }
}
