using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupsSettingsConfiguration : IEntityTypeConfiguration<WorkgroupsSettings>
{
    public void Configure(EntityTypeBuilder<WorkgroupsSettings> builder)
    {
        builder.ToTable("workgroups_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.RootDriveFolderId).HasMaxLength(1000).IsRequired();
    }
}
