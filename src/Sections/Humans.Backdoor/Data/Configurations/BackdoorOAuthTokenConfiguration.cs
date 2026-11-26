using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Backdoor.Data.Configurations;

internal sealed class BackdoorOAuthTokenConfiguration : IEntityTypeConfiguration<BackdoorOAuthToken>
{
    public void Configure(EntityTypeBuilder<BackdoorOAuthToken> builder)
    {
        builder.ToTable("backdoor_oauth_tokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.ClientId).HasMaxLength(100);
        builder.Property(t => t.Type).HasMaxLength(150).IsRequired();
        builder.Property(t => t.Status).HasMaxLength(50).IsRequired();
        builder.Property(t => t.ReferenceId).HasMaxLength(100);

        builder.HasOne<BackdoorOAuthConnection>()
            .WithMany()
            .HasForeignKey(t => t.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.ReferenceId).IsUnique();
        builder.HasIndex(t => t.UserId);
    }
}
