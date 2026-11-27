using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Backdoor.Data.Configurations;

internal sealed class BackdoorOAuthConnectionConfiguration : IEntityTypeConfiguration<BackdoorOAuthConnection>
{
    public void Configure(EntityTypeBuilder<BackdoorOAuthConnection> builder)
    {
        builder.ToTable("backdoor_oauth_connections");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ClientId).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Status).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Type).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Scopes).HasMaxLength(500).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();

        builder.HasIndex(c => c.UserId);
    }
}
