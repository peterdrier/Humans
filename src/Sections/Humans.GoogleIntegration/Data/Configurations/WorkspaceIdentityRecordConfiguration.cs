using Humans.GoogleIntegration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.GoogleIntegration.Data.Configurations;

internal sealed class WorkspaceIdentityRecordConfiguration : IEntityTypeConfiguration<WorkspaceIdentityRecord>
{
    public void Configure(EntityTypeBuilder<WorkspaceIdentityRecord> b)
    {
        b.ToTable("google_workspace_identities");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).ValueGeneratedNever();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();
    }
}
