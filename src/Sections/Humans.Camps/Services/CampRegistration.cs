using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampRegistration(
    ICampRepository repo,
    IAuditLogService auditLog,
    ISystemTeamSync systemTeamSync,
    CampSeasonLifecycle seasons,
    IClock clock,
    ILogger<CampService> logger) : IApplicationService
{
    internal const int MaxHistoricalNameLength = 256;
    private const int MaxCampSlugLength = 256;

    public async Task<CampWriteResult<Camp>> CreateCampAsync(
        Guid createdByUserId, string name, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        CampSeasonData seasonData, List<string>? historicalNames, Guid eventSettingsId,
        CancellationToken cancellationToken = default)
    {
        var targetEvent = (await seasons.GetRegistrationByEventAsync(cancellationToken))
            .FirstOrDefault(r => r.EventSettingsId == eventSettingsId);
        if (targetEvent is null)
            return new(null, "Camps_Flash_SeasonNotOpen");
        if (historicalNames?.Any(string.IsNullOrWhiteSpace) == true)
            return new(null, "Camps_Flash_NameRequired");
        if (historicalNames?.Any(n => n.Trim().Length > MaxHistoricalNameLength) == true)
            return new(null, "Camps_Validation_HistoricalNameLength");

        var slug = SlugHelper.GenerateSlug(name);
        if (slug.Length == 0)
            slug = "camp";
        if (SlugHelper.IsReservedCampSlug(slug))
        {
            return new(null, "Camps_Flash_ReservedName");
        }

        var baseSlug = slug;
        var suffix = 2;
        while (await repo.SlugExistsAsync(slug, cancellationToken))
        {
            var suffixText = "-" + suffix.ToString(CultureInfo.InvariantCulture);
            var prefixLength = Math.Min(baseSlug.Length, MaxCampSlugLength - suffixText.Length);
            slug = baseSlug[..prefixLength].TrimEnd('-') + suffixText;
            suffix++;
        }

        var now = clock.GetCurrentInstant();
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            ContactEmail = contactEmail,
            ContactPhone = contactPhone,
            WebOrSocialUrl = links is { Count: > 0 } ? null : webOrSocialUrl,
            Links = links,
            IsSwissCamp = isSwissCamp,
            TimesAtNowhere = timesAtNowhere,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        var season = CampSeasonLifecycle.CreateSeasonFromData(camp.Id, targetEvent.Year, name, seasonData, now);
        season.EventSettingsId = eventSettingsId;

        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = createdByUserId,
            Status = CampMemberStatus.Active,
            RequestedAt = now,
            ConfirmedAt = now,
            ConfirmedByUserId = createdByUserId,
        };

        var leadDef = await repo.GetSpecialDefinitionAsync(CampSpecialRole.Lead, cancellationToken);
        CampRoleAssignment? leadAssignment = null;
        if (leadDef is not null)
        {
            leadAssignment = new CampRoleAssignment
            {
                Id = Guid.NewGuid(),
                CampSeasonId = season.Id,
                CampRoleDefinitionId = leadDef.Id,
                CampMemberId = member.Id,
                AssignedAt = now,
                AssignedByUserId = createdByUserId,
            };
        }
        else
        {
            logger.LogWarning(
                "Camp Lead role definition missing while creating camp {CampId}; creator added as Active member without a lead assignment. Run 'Seed system roles'.",
                camp.Id);
        }

        List<CampHistoricalName>? historicalNameEntities = null;
        if (historicalNames is { Count: > 0 })
        {
            historicalNameEntities = historicalNames.Select(oldName => new CampHistoricalName
            {
                Id = Guid.NewGuid(),
                CampId = camp.Id,
                Name = oldName.Trim(),
                Source = CampNameSource.Manual,
                CreatedAt = now
            }).ToList();
        }

        await repo.CreateCampAsync(camp, season, member, leadAssignment, historicalNameEntities, cancellationToken);

        await auditLog.LogAsync(
            CampsAuditActions.CampCreated, nameof(Camp), camp.Id,
            $"Registered camp '{name}' for {targetEvent.EventName}",
            createdByUserId);

        await systemTeamSync.SyncMembershipForUserAsync(
            createdByUserId, SystemTeamType.BarrioLeads, cancellationToken);

        return new(camp);
    }
}
