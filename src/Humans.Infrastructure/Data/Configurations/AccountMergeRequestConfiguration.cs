using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class AccountMergeRequestConfiguration : IEntityTypeConfiguration<AccountMergeRequest>
{
    public void Configure(EntityTypeBuilder<AccountMergeRequest> builder)
    {
        builder.ToTable("account_merge_requests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.AdminNotes)
            .HasMaxLength(4000);

        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasOne(r => r.TargetUser)
            .WithMany()
            .HasForeignKey(r => r.TargetUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.SourceUser)
            .WithMany()
            .HasForeignKey(r => r.SourceUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ResolvedByUser)
            .WithMany()
            .HasForeignKey(r => r.ResolvedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.TargetUserId);
        builder.HasIndex(r => r.SourceUserId);
    }
}
