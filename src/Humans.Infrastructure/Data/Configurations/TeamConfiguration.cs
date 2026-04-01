using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Infrastructure.Data.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    // Fixed seed timestamp so migrations are deterministic
    private static readonly Instant SeedTimestamp = Instant.FromUtc(2026, 2, 4, 23, 52, 37);

    private static readonly JsonSerializerOptions JsonEnumOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(t => t.Description)
            .HasMaxLength(2000);

        builder.Property(t => t.Slug)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(t => t.RequiresApproval)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(t => t.SystemTeamType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(t => t.GoogleGroupPrefix)
            .HasMaxLength(64);

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.Property(t => t.UpdatedAt)
            .IsRequired();

        builder.Property(t => t.CustomSlug)
            .HasMaxLength(256);

        builder.Property(t => t.IsPublicPage)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(t => t.ShowCoordinatorsOnPublicPage)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(t => t.HasBudget)
            .IsRequired();

        builder.Property(t => t.IsHidden)
            .IsRequired();

        builder.Property(t => t.PageContent)
            .HasMaxLength(50000);

        builder.Property(t => t.PageContentUpdatedByUserId);

        builder.Property(t => t.CallsToAction).HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, JsonEnumOptions),
                v => v == null ? null : JsonSerializer.Deserialize<List<CallToAction>>(v, JsonEnumOptions),
                new ValueComparer<List<CallToAction>>(
                    (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                    v => v == null ? 0 : v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.Text, item.Url, item.Style)),
                    v => v == null ? null! : v.Select(c => new CallToAction { Text = c.Text, Url = c.Url, Style = c.Style }).ToList()));

        builder.Property(t => t.ParentTeamId);

        builder.HasOne(t => t.ParentTeam)
            .WithMany(t => t.ChildTeams)
            .HasForeignKey(t => t.ParentTeamId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Members)
            .WithOne(tm => tm.Team)
            .HasForeignKey(tm => tm.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.JoinRequests)
            .WithOne(jr => jr.Team)
            .HasForeignKey(jr => jr.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.GoogleResources)
            .WithOne(gr => gr.Team)
            .HasForeignKey(gr => gr.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(t => t.Slug)
            .IsUnique();

        builder.HasIndex(t => t.IsActive);

        builder.HasIndex(t => t.SystemTeamType);

        builder.HasIndex(t => t.CustomSlug)
            .IsUnique()
            .HasFilter("\"CustomSlug\" IS NOT NULL");

        builder.HasIndex(t => t.GoogleGroupPrefix)
            .IsUnique()
            .HasFilter("\"GoogleGroupPrefix\" IS NOT NULL");

        // Ignore computed properties
        builder.Ignore(t => t.IsSystemTeam);
        builder.Ignore(t => t.GoogleGroupEmail);
        builder.Ignore(t => t.DisplayName);

        // Seed system teams
        builder.HasData(
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000001"),
                Name = "Volunteers",
                Description = "All active volunteers with signed required documents",
                Slug = "volunteers",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Volunteers,
                GoogleGroupPrefix = (string?)null,
                ParentTeamId = (Guid?)null,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
                IsPublicPage = false,
                PageContent = (string?)null,
                PageContentUpdatedAt = (Instant?)null,
                PageContentUpdatedByUserId = (Guid?)null,
                CallsToAction = (List<CallToAction>?)null,
                CustomSlug = (string?)null,
                ShowCoordinatorsOnPublicPage = true,
                HasBudget = false,
                IsHidden = false
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000002"),
                Name = "Coordinators",
                Description = "All team coordinators",
                Slug = "coordinators",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Coordinators,
                GoogleGroupPrefix = (string?)null,
                ParentTeamId = (Guid?)null,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
                IsPublicPage = false,
                PageContent = (string?)null,
                PageContentUpdatedAt = (Instant?)null,
                PageContentUpdatedByUserId = (Guid?)null,
                CallsToAction = (List<CallToAction>?)null,
                CustomSlug = (string?)null,
                ShowCoordinatorsOnPublicPage = true,
                HasBudget = false,
                IsHidden = false
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000003"),
                Name = "Board",
                Description = "Board members with active role assignments",
                Slug = "board",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Board,
                GoogleGroupPrefix = (string?)null,
                ParentTeamId = (Guid?)null,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
                IsPublicPage = false,
                PageContent = (string?)null,
                PageContentUpdatedAt = (Instant?)null,
                PageContentUpdatedByUserId = (Guid?)null,
                CallsToAction = (List<CallToAction>?)null,
                CustomSlug = (string?)null,
                ShowCoordinatorsOnPublicPage = true,
                HasBudget = false,
                IsHidden = false
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000004"),
                Name = "Asociados",
                Description = "Voting members with approved asociado applications",
                Slug = "asociados",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Asociados,
                GoogleGroupPrefix = (string?)null,
                ParentTeamId = (Guid?)null,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
                IsPublicPage = false,
                PageContent = (string?)null,
                PageContentUpdatedAt = (Instant?)null,
                PageContentUpdatedByUserId = (Guid?)null,
                CallsToAction = (List<CallToAction>?)null,
                CustomSlug = (string?)null,
                ShowCoordinatorsOnPublicPage = true,
                HasBudget = false,
                IsHidden = false
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000005"),
                Name = "Colaboradors",
                Description = "Active contributors with approved colaborador applications",
                Slug = "colaboradors",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.Colaboradors,
                GoogleGroupPrefix = (string?)null,
                ParentTeamId = (Guid?)null,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
                IsPublicPage = false,
                PageContent = (string?)null,
                PageContentUpdatedAt = (Instant?)null,
                PageContentUpdatedByUserId = (Guid?)null,
                CallsToAction = (List<CallToAction>?)null,
                CustomSlug = (string?)null,
                ShowCoordinatorsOnPublicPage = true,
                HasBudget = false,
                IsHidden = false
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0001-000000000006"),
                Name = "Barrio Leads",
                Description = "All active camp leads across all camps",
                Slug = "barrio-leads",
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.BarrioLeads,
                GoogleGroupPrefix = (string?)null,
                ParentTeamId = (Guid?)null,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
                IsPublicPage = false,
                PageContent = (string?)null,
                PageContentUpdatedAt = (Instant?)null,
                PageContentUpdatedByUserId = (Guid?)null,
                CallsToAction = (List<CallToAction>?)null,
                CustomSlug = (string?)null,
                ShowCoordinatorsOnPublicPage = true,
                HasBudget = false,
                IsHidden = false
            });
    }
}
