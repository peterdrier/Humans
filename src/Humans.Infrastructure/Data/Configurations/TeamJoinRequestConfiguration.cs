using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class TeamJoinRequestConfiguration : IEntityTypeConfiguration<TeamJoinRequest>
{
    public void Configure(EntityTypeBuilder<TeamJoinRequest> builder)
    {
        builder.ToTable("team_join_requests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.Message)
            .HasMaxLength(2000);

        builder.Property(r => r.RequestedAt)
            .IsRequired();

        builder.Property(r => r.ReviewNotes)
            .HasMaxLength(2000);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ReviewedByUser)
            .WithMany()
            .HasForeignKey(r => r.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.StateHistory)
            .WithOne(sh => sh.TeamJoinRequest)
            .HasForeignKey(sh => sh.TeamJoinRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.TeamId);
        builder.HasIndex(r => r.UserId);
        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => new { r.TeamId, r.UserId, r.Status });
    }
}
