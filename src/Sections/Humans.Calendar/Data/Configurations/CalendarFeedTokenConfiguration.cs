using Humans.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Calendar.Data.Configurations;

internal sealed class CalendarFeedTokenConfiguration : IEntityTypeConfiguration<CalendarFeedToken>
{
    public void Configure(EntityTypeBuilder<CalendarFeedToken> builder)
    {
        builder.ToTable("calendar_feed_tokens");

        // One token per member, so the member is the key — a bare cross-section id, not
        // an FK. ValueGeneratedNever so a caller-supplied Guid is always what lands:
        // without it EF's convention would invent an id for a Guid.Empty write instead
        // of failing loudly.
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).ValueGeneratedNever();

        builder.Property(x => x.Token).IsRequired();

        // No index on Token: validation is a lookup by UserId plus a compare
        // (the uid is in the URL), never a lookup by token.
    }
}
