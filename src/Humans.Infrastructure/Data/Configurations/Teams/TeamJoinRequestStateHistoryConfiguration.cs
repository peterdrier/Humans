using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Teams;

public class TeamJoinRequestStateHistoryConfiguration : IEntityTypeConfiguration<TeamJoinRequestStateHistory>
{
    public void Configure(EntityTypeBuilder<TeamJoinRequestStateHistory> builder)
    {
        builder.ToTable("team_join_request_state_history");

        builder.HasKey(sh => sh.Id);

        builder.Property(sh => sh.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(sh => sh.ChangedAt)
            .IsRequired();

        builder.Property(sh => sh.Notes)
            .HasMaxLength(2000);

#pragma warning disable CS0618 // Obsolete cross-domain nav kept so EF FK constraint stays modelled.
        builder.HasOne(sh => sh.ChangedByUser)
            .WithMany()
            .HasForeignKey(sh => sh.ChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618

        builder.HasIndex(sh => sh.TeamJoinRequestId);
        builder.HasIndex(sh => sh.ChangedAt);
    }
}
