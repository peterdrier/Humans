using System.Security.Cryptography;
using System.Text;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Configuration;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Web.Infrastructure;

/// <summary>
/// Dev-only persona seeding helper. Owns the writes that
/// <see cref="Controllers.DevLoginController"/> used to do directly against
/// <see cref="HumansDbContext"/>.
///
/// Cross-section writes (User, Profile, UserEmail) flow through the owning
/// section's services per design-rules §2c
/// (<see cref="UserManager{TUser}"/> / <see cref="IProfileService"/> /
/// <see cref="IUserEmailService"/>). All dev fixtures (system-team
/// memberships, dev test department, dev barrio camp/season/lead, city-planning
/// team, role assignments, sample contact fields) also go through section
/// ownership services, so this seeder no longer depends on DbContext writes.
/// </summary>
public sealed class DevPersonaSeeder
{
    private readonly UserManager<User> _userManager;
    private readonly IProfileService _profileService;
    private readonly IUserEmailService _userEmailService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IUserInfoInvalidator _userInfoInvalidator;
    private readonly ITeamService _teamService;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IUserService _userService;
    private readonly ICampService _campService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly IOptions<CityPlanningOptions> _cityPlanningOptions;
    private readonly ILogger<DevPersonaSeeder> _logger;

    public DevPersonaSeeder(
        UserManager<User> userManager,
        IProfileService profileService,
        IUserEmailService userEmailService,
        IContactFieldService contactFieldService,
        IRoleAssignmentService roleAssignmentService,
        IUserInfoInvalidator userInfoInvalidator,
        ITeamService teamService,
        ISystemTeamSync systemTeamSync,
        IUserService userService,
        ICampService campService,
        IClock clock,
        IMemoryCache cache,
        IOptions<CityPlanningOptions> cityPlanningOptions,
        ILogger<DevPersonaSeeder> logger)
    {
        _userManager = userManager;
        _profileService = profileService;
        _userEmailService = userEmailService;
        _contactFieldService = contactFieldService;
        _roleAssignmentService = roleAssignmentService;
        _userInfoInvalidator = userInfoInvalidator;
        _teamService = teamService;
        _systemTeamSync = systemTeamSync;
        _userService = userService;
        _campService = campService;
        _clock = clock;
        _cache = cache;
        _cityPlanningOptions = cityPlanningOptions;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the dev persona's User + Profile + verified UserEmail exist.
    /// Returns the resolved user id (handles legacy hardcoded GUIDs).
    /// </summary>
    public async Task<Guid> EnsurePersonaAsync(string slug, string displayNameSuffix, Guid id)
    {
        var existing = await _userManager.FindByIdAsync(id.ToString());
        if (existing is not null)
            return existing.Id;

        var email = $"dev-{slug}@localhost";

        // Legacy personas may exist with old hardcoded GUIDs â€” reuse them.
        var byEmailUserId = await _userEmailService.GetUserIdByVerifiedEmailAsync(email);
        if (byEmailUserId is not null)
        {
            _logger.LogInformation("DEV: found legacy persona {Email} ({OldId}), reusing", email, byEmailUserId.Value);
            return byEmailUserId.Value;
        }

        var now = _clock.GetCurrentInstant();
        var displayName = $"Dev {displayNameSuffix}";

        // Guest persona: bare user with no profile, teams, or roles
        if (string.Equals(slug, "guest", StringComparison.OrdinalIgnoreCase))
        {
            await SeedProfilelessUserAsync(id, email, displayName, now);
            return id;
        }

        var nameParts = displayNameSuffix.Split(' ', 2);
        var firstName = nameParts[0];
        var lastName = nameParts.Length > 1 ? nameParts[1] : displayNameSuffix;

        // Determine role assignments
        var isCoordinatorPersona = string.Equals(slug, "coordinator", StringComparison.OrdinalIgnoreCase);
        var roleName = isCoordinatorPersona ? null : RoleNameFromSlug(slug);
        var roles = roleName is not null ? new[] { roleName } : Array.Empty<string>();

        var user = new User
        {
            Id = id,
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("Failed to create dev persona {Email}: {Errors}",
                email, string.Join(", ", result.Errors.Select(e => e.Description)));
            return id;
        }

        // Seed primary verified UserEmail via the canonical service path.
        await _userEmailService.AddVerifiedEmailAsync(id, email);

        // Seed the Profile via the canonical service path. SaveProfileAsync
        // creates the Profile row when missing, sets all fields, and lifts
        // the lifecycle marker out of Stub. We bypass the dev-only IsApproved
        // shortcut by setting it explicitly post-save.
        var saveRequest = new ProfileSaveRequest(
            BurnerName: displayName,
            FirstName: firstName,
            LastName: lastName,
            City: "Barcelona",
            CountryCode: "ES",
            Latitude: null,
            Longitude: null,
            PlaceId: null,
            Bio: $"Dev persona for testing the {displayNameSuffix} role.",
            Pronouns: "they/them",
            ContributionInterests: null,
            BoardNotes: null,
            BirthdayMonth: 6,
            BirthdayDay: 15,
            EmergencyContactName: null,
            EmergencyContactPhone: null,
            EmergencyContactRelationship: null,
            NoPriorBurnExperience: false,
            ProfilePictureData: null,
            ProfilePictureContentType: null,
            RemoveProfilePicture: false);

        var profileId = await _profileService.SaveProfileAsync(id, displayName, saveRequest, "en");

        // Mark approved + cleared so the dev persona skips the consent gate
        // and lands on the dashboard. Routes through ProfileService so the
        // CachingUserService decorator handles the UserInfo cache
        // refresh atomically with the DB write (issue #474 â€” Profiles is the
        // single writer to the profile state fields).
        var consentCheckResult = await _profileService.RecordConsentCheckAsync(
            id, reviewerId: id, ConsentCheckStatus.Cleared,
            notes: "Dev persona — auto-seeded");
        if (!consentCheckResult.Success)
            _logger.LogWarning(
                "DEV: consent-check approval failed for persona {UserId}: {ErrorKey}",
                id, consentCheckResult.ErrorKey);

        // Seed sample contact fields through the profile service.
        await _contactFieldService.SaveContactFieldsAsync(
            profileId,
            [
                new ContactFieldEditDto(
                    Id: null,
                    FieldType: ContactFieldType.Signal,
                    CustomLabel: null,
                    Value: $"+34 600 000 {id.ToString()[..3]}",
                    Visibility: ContactFieldVisibility.AllActiveProfiles,
                    DisplayOrder: 0),
                new ContactFieldEditDto(
                    Id: null,
                    FieldType: ContactFieldType.Telegram,
                    CustomLabel: null,
                    Value: $"@dev_{slug}",
                    Visibility: ContactFieldVisibility.MyTeams,
                    DisplayOrder: 1)
            ]);

        // All seeded personas should be in Volunteers (unless explicitly restricted later).
        await _systemTeamSync.SyncMembershipForUserAsync(id, SystemTeamType.Volunteers);

        if (IsBarrioLeadSlug(slug))
        {
            await _systemTeamSync.SyncMembershipForUserAsync(id, SystemTeamType.BarrioLeads);
        }

        // Board membership has no per-user sync arm in SystemTeamSyncJob, so apply directly.
        if (roleName is not null && string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _teamService.ApplySystemTeamMembershipDeltaAsync(SystemTeamIds.Board, [id], [], now);
        }

        foreach (var role in roles)
        {
            await EnsureSeededRoleAsync(id, role, id);
        }

        _cache.InvalidateUserAccess(id);
        await _userInfoInvalidator.InvalidateAsync(id);

        _logger.LogInformation(
            "DEV: seeded persona {Email} with roles [{Roles}]",
            email,
            string.Join(", ", roles));

        return id;
    }

    /// <summary>
    /// Ensures a governance role assignment exists. Existing active assignments are accepted.
    /// </summary>
    private async Task EnsureSeededRoleAsync(Guid userId, string roleName, Guid assignerId)
    {
        var result = await _roleAssignmentService.AssignRoleAsync(
            userId, roleName, assignerId, "Dev persona — auto-seeded");

        if (!result.Success && !string.Equals(result.ErrorKey, "RoleAlreadyActive", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "DEV: failed to seed role {Role} for user {UserId}: {ErrorKey}",
                roleName, userId, result.ErrorKey);
        }
    }

    /// <summary>
    /// Mints a brand-new profileless guest user (random id + unique email) and
    /// returns its id. Each call creates a fresh account so multiple testers
    /// can run the onboarding widget in parallel without colliding on a single
    /// shared Guest account.
    /// </summary>
    public async Task<Guid> EnsureFreshGuestAsync(string displayNameSuffix)
    {
        var newId = Guid.NewGuid();
        var shortId = newId.ToString("N")[..8];
        var email = $"dev-guest-{shortId}@localhost";
        var displayName = $"Dev {displayNameSuffix} {shortId}";
        var now = _clock.GetCurrentInstant();
        await SeedProfilelessUserAsync(newId, email, displayName, now);
        return newId;
    }

    /// <summary>
    /// Seeds a profileless user â€” just User + UserEmail, no Profile, no teams, no roles.
    /// Used for testing the Guest dashboard and profileless account flows.
    /// </summary>
    private async Task SeedProfilelessUserAsync(Guid id, string email, string displayName, Instant now)
    {
        var user = new User
        {
            Id = id,
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("Failed to create dev guest persona {Email}: {Errors}",
                email, string.Join(", ", result.Errors.Select(e => e.Description)));
            return;
        }

        await _userEmailService.AddVerifiedEmailAsync(id, email);

        _logger.LogInformation("DEV: seeded profileless guest persona {Email} ({Id})", email, id);
    }

    /// <summary>
    /// Seeds a test barrio camp + season + lead for the barrio-N-lead persona.
    /// </summary>
    public async Task EnsureBarrioCampAsync(string personaSlug, Guid leadUserId)
    {
        var campSlug = personaSlug[..^"-lead".Length];
        var campName = campSlug.Replace("-", " ", StringComparison.Ordinal);
        campName = string.Concat(campName[..1].ToUpperInvariant(), campName.AsSpan(1));

        var year = (await _campService.GetSettingsAsync()).PublicYear;
        if (year <= 0)
            year = _clock.GetCurrentInstant().InUtc().Date.Year;

        var camp = await _campService.GetCampBySlugAsync(campSlug);
        var created = false;

        if (camp is null)
        {
            var seasonData = new CampSeasonData(
                BlurbLong: $"{campName} is a development test barrio used for local and preview environment testing. Feel free to edit this description.",
                BlurbShort: $"A dev test barrio ({campName}).",
                Languages: "English, Spanish",
                AcceptingMembers: YesNoMaybe.Yes,
                KidsWelcome: YesNoMaybe.No,
                KidsVisiting: KidsVisitingPolicy.DaytimeOnly,
                KidsAreaDescription: null,
                HasPerformanceSpace: PerformanceSpaceStatus.No,
                PerformanceTypes: string.Empty,
                Vibes: [],
                AdultPlayspace: AdultPlayspacePolicy.No,
                MemberCount: 42,
                SpaceRequirement: SpaceSize.Sqm600,
                SoundZone: null,
                ElectricalGrid: null);

            await _campService.CreateCampAsync(
                leadUserId,
                campName,
                $"dev-{campSlug}@localhost",
                "+34 600 000 000",
                webOrSocialUrl: null,
                links: [],
                isSwissCamp: false,
                timesAtNowhere: 0,
                seasonData,
                historicalNames: [],
                year);

            _logger.LogInformation("DEV: seeded camp {Slug}", campSlug);
            created = true;
            camp = await _campService.GetCampBySlugAsync(campSlug);
        }

        if (camp is null)
        {
            _logger.LogError("DEV: failed to resolve barrio camp {Slug} after creation", campSlug);
            return;
        }

        var currentYearSeason = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (currentYearSeason is null)
        {
            try
            {
                await _campService.OptInToSeasonAsync(camp.Id, year);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("has a season", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "DEV: barrio camp {Slug} already has a season for {Year}: {Message}",
                    campSlug, year, ex.Message);
            }

            camp = await _campService.GetCampBySlugAsync(campSlug);
            if (camp is null)
            {
                _logger.LogError("DEV: failed to resolve barrio camp {Slug} after season creation", campSlug);
                return;
            }

            currentYearSeason = camp.Seasons.FirstOrDefault(s => s.Year == year);
        }

        if (currentYearSeason is not null && currentYearSeason.Status == CampSeasonStatus.Pending)
        {
            try
            {
                await _campService.ApproveSeasonAsync(currentYearSeason.Id, leadUserId, "Dev persona seed");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot approve a season", StringComparison.Ordinal))
            {
                // Idempotent: another seed/admin path likely moved it first.
                _logger.LogInformation(
                    "DEV: barrio camp season {SeasonId} approve skipped — already moved past Pending",
                    currentYearSeason.Id);
            }
        }

        var leadAdded = await EnsureCampLeadAsync(camp, leadUserId);
        if (leadAdded)
        {
            _logger.LogInformation(
                "DEV: ensured barrio lead for {CampId} user {UserId}",
                camp.Id, leadUserId);
        }

        // Sync BarrioLeads membership whenever the camp was just created OR the lead
        // was just added — for pre-existing camps the user could have become a lead
        // without a downstream sync trigger otherwise.
        if (created || leadAdded)
        {
            await _systemTeamSync.SyncMembershipForUserAsync(leadUserId, SystemTeamType.BarrioLeads);
        }
    }

    private async Task<bool> EnsureCampLeadAsync(CampLookup camp, Guid leadUserId)
    {
        if (camp.Leads.Any(l => l.UserId == leadUserId))
            return false;

        try
        {
            await _campService.AddLeadAsync(camp.Id, leadUserId);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already an active lead", StringComparison.Ordinal))
        {
            // Idempotent: lead was added between the pre-check and AddLeadAsync.
            _logger.LogInformation(
                "DEV: camp lead {UserId} for {CampId} already active — skipping",
                leadUserId, camp.Id);
            return false;
        }
    }

    /// <summary>
    /// Ensures the coordinator persona's test department and sub-team exist with active memberships.
    /// </summary>
    public async Task EnsureCoordinatorTeamsAsync(Guid coordinatorUserId)
    {
        var now = _clock.GetCurrentInstant();
        var changed = false;

        var department = await _teamService.GetTeamBySlugAsync("dev-test-department");
        if (department is null)
        {
            department = await _teamService.CreateTeamAsync(
                "Dev Test Department",
                "Test department for coordinator e2e tests",
                requiresApproval: true,
                cancellationToken: default);
            changed = true;
        }

        var subTeam = await _teamService.GetTeamBySlugAsync("dev-test-subteam");
        if (subTeam is null)
        {
            subTeam = await _teamService.CreateTeamAsync(
                "Dev Test SubTeam",
                "Test sub-team for coordinator e2e tests",
                requiresApproval: true,
                parentTeamId: department.Id,
                cancellationToken: default);
            changed = true;
        }

        changed |= await EnsureSeededTeamMembershipAsync(department.Id, coordinatorUserId, TeamMemberRole.Coordinator, now);
        changed |= await EnsureSeededTeamMembershipAsync(subTeam.Id, coordinatorUserId, TeamMemberRole.Member, now);

        if (changed)
        {
            _teamService.InvalidateActiveTeamsCache();
            _cache.InvalidateUserAccess(coordinatorUserId);
            // Team membership changes ripple into UserInfo (active-teams shape)
            await _userInfoInvalidator.InvalidateAsync(coordinatorUserId);
            _logger.LogInformation(
                "DEV: ensured coordinator teams — department {DeptId}, sub-team {SubTeamId}",
                department.Id, subTeam.Id);
        }
    }

    private async Task<bool> EnsureSeededTeamMembershipAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole expectedRole,
        Instant now)
    {
        var team = await _teamService.GetTeamAsync(teamId);
        var existingMember = team?.Members.FirstOrDefault(m => m.UserId == userId);
        if (existingMember is null)
        {
            await _teamService.AddSeededMemberAsync(teamId, userId, expectedRole, now);
            return true;
        }

        if (existingMember.Role == expectedRole)
            return false;

        await _teamService.SetMemberRoleAsync(teamId, userId, expectedRole, userId);
        return true;
    }

    /// <summary>
    /// Ensures the city planning team (from config) exists and the user is a coordinator of it.
    /// </summary>
    public async Task EnsureCityPlanningTeamAsync(Guid userId)
    {
        var teamSlug = _cityPlanningOptions.Value.CityPlanningTeamSlug;
        if (string.IsNullOrEmpty(teamSlug))
        {
            _logger.LogWarning("DEV: CityPlanning:CityPlanningTeamSlug is not configured, skipping city planning team seeding");
            return;
        }

        var changed = false;
        var team = await _teamService.GetTeamBySlugAsync(teamSlug);
        if (team is null)
        {
            if (!string.Equals(teamSlug, "city-planning", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "DEV: city-planning config slug '{Slug}' is not supported by team service APIs; cannot create deterministic team with custom slug",
                    teamSlug);
                return;
            }

            team = await _teamService.CreateTeamAsync(
                "City Planning",
                "Dev-seeded city planning team",
                requiresApproval: false);
            changed = true;
            _logger.LogInformation("DEV: seeded city planning team {Slug}", teamSlug);
        }

        changed |= await EnsureSeededTeamMembershipAsync(team.Id, userId, TeamMemberRole.Coordinator, _clock.GetCurrentInstant());

        if (changed)
        {
            _teamService.InvalidateActiveTeamsCache();
            _cache.InvalidateUserAccess(userId);
            // Team membership changes ripple into UserInfo (active-teams shape)
            await _userInfoInvalidator.InvalidateAsync(userId);
        }
    }

    /// <summary>
    /// Returns the first 100 users (display name ordered) for the dev user-chooser view.
    /// Filters out ephemeral guest accounts minted by the Guest dev-login button.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string DisplayName, string Email)>> GetUsersForChooserAsync(
        CancellationToken ct = default)
    {
        var users = await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);

        IReadOnlyList<(Guid Id, string DisplayName, string Email)> result = users
            .Where(u => !(u.Email ?? string.Empty).StartsWith("dev-guest-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.DisplayName, StringComparer.Ordinal)
            .Take(100)
            .Select(u => (u.Id, u.DisplayName, u.Email ?? string.Empty))
            .ToList();
        return result;
    }

    public static bool IsBarrioLeadSlug(string slug) =>
        slug.EndsWith("-lead", StringComparison.OrdinalIgnoreCase) &&
        slug.StartsWith("barrio-", StringComparison.OrdinalIgnoreCase);

    public static bool IsCityPlanningSlug(string slug) =>
        string.Equals(slug, "city-planning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic GUID from persona slug â€” stable across restarts for idempotent seeding.
    /// </summary>
    public static Guid PersonaGuid(string slug)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"dev-persona:{slug}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Resolves a persona slug back to a RoleNames constant, or null for "volunteer".
    /// </summary>
    private static string? RoleNameFromSlug(string slug)
    {
        if (string.Equals(slug, "volunteer", StringComparison.OrdinalIgnoreCase))
            return null;

        return typeof(RoleNames)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .FirstOrDefault(r => string.Equals(PascalToKebab(r), slug, StringComparison.OrdinalIgnoreCase));
    }

    private static string PascalToKebab(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}
