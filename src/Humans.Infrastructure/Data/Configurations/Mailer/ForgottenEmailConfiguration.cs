using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Mailer;

public class ForgottenEmailConfiguration : IEntityTypeConfiguration<ForgottenEmail>
{
    public void Configure(EntityTypeBuilder<ForgottenEmail> b)
    {
        b.ToTable("forgotten_emails");
        b.HasKey(x => x.Id);

        b.Property(x => x.EmailHash)
            .IsRequired()
            .HasMaxLength(64);

        b.Property(x => x.AnonymizedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        b.HasIndex(x => x.EmailHash);
        b.HasIndex(x => new { x.UserId, x.EmailHash }).IsUnique();
        b.HasIndex(x => x.AnonymizedAt);
    }
}
