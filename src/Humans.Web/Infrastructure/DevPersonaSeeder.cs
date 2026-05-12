using System.Security.Cryptography;
using System.Text;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Camps;
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
/// Dev-only persona seeding helper. Web orchestration stays here; writes flow
/// through the owning section services.
/// </summary>
public sealed class DevPersonaSeeder
{
    private readonly UserManager<User> _userManager;
    private readonly IProfileService _profileService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ICampService _campService;
    private readonly IUserService _userService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly IOptions<CityPlanningOptions> _cityPlanningOptions;
    private readonly ILogger<DevPersonaSeeder> _logger;

    public DevPersonaSeeder(
        UserManager<User> userManager,
        IProfileService profileService,
        IContactFieldService contactFieldService,
        IUserEmailService userEmailService,
        IFullProfileInvalidator fullProfileInvalidator,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        ICampService campService,
        IUserService userService,
        IClock clock,
        IMemoryCache cache,
        IOptions<CityPlanningOptions> cityPlanningOptions,
        ILogger<DevPersonaSeeder> logger)
    {
        _userManager = userManager;
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _userEmailService = userEmailService;
        _fullProfileInvalidator = fullProfileInvalidator;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _campService = campService;
        _userService = userService;
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

        // Legacy personas may exist with old hardcoded GUIDs - reuse them.
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

        // Determine role and team assignments
        var isCoordinatorPersona = string.Equals(slug, "coordinator", StringComparison.OrdinalIgnoreCase);
        var roleName = isCoordinatorPersona ? null : RoleNameFromSlug(slug);
        var roles = roleName is not null ? new[] { roleName } : Array.Empty<string>();
        Guid[] teams;
        if (roleName is not null && string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal))
            teams = [SystemTeamIds.Volunteers, SystemTeamIds.Board];
        else if (IsBarrioLeadSlug(slug))
            teams = [SystemTeamIds.Volunteers, SystemTeamIds.BarrioLeads];
        else
            teams = [SystemTeamIds.Volunteers];

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
        // CachingProfileService decorator handles the FullProfile cache
        // refresh atomically with the DB write (issue #474 - Profiles is the
        // single writer to the profile state fields).
        var consentCheckResult = await _profileService.RecordConsentCheckAsync(
            id, reviewerId: id, ConsentCheckStatus.Cleared,
            notes: "Dev persona - auto-seeded");
        if (!consentCheckResult.Success)
            _logger.LogWarning(
                "DEV: consent-check approval failed for persona {UserId}: {ErrorKey}",
                id, consentCheckResult.ErrorKey);

        // Seed sample contact fields so profile page exercises the contact rendering path.
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
                    DisplayOrder: 1),
            ]);

        foreach (var teamId in teams)
        {
            if (await _teamService.IsUserMemberOfTeamAsync(teamId, id))
            {
                continue;
            }

            await _teamService.ApplySystemTeamMembershipDeltaAsync(
                teamId,
                [id],
                [],
                now);
        }

        foreach (var role in roles)
        {
            var assignResult = await _roleAssignmentService.AssignRoleAsync(
                id,
                role,
                id,
                "Dev persona - auto-seeded");
            if (!assignResult.Success)
            {
                _logger.LogWarning(
                    "DEV: role assignment failed for persona {UserId}, role {Role}: {ErrorKey}",
                    id,
                    role,
                    assignResult.ErrorKey);
            }
        }
        _cache.InvalidateUserAccess(id);

        _logger.LogInformation("DEV: seeded persona {Email} with roles [{Roles}] and teams [{Teams}]",
            email, string.Join(", ", roles), string.Join(", ", teams.Select(t => t)));

        return id;
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
    /// Seeds a profileless user - just User + UserEmail, no Profile, no teams, no roles.
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
        var camp = await _campService.GetCampBySlugAsync(campSlug);
        if (camp is null)
        {
            var created = await _campService.CreateCampAsync(
                leadUserId,
                campName,
                $"dev-{campSlug}@localhost",
                "+34 600 000 000",
                webOrSocialUrl: null,
                links: null,
                isSwissCamp: false,
                timesAtNowhere: 0,
                new CampSeasonData(
                    BlurbLong: $"{campName} is a development test barrio used for local and preview environment testing. Feel free to edit this description.",
                    BlurbShort: $"A dev test barrio ({campName}).",
                    Languages: "English, Spanish",
                    AcceptingMembers: YesNoMaybe.Yes,
                    KidsWelcome: YesNoMaybe.Maybe,
                    KidsVisiting: KidsVisitingPolicy.DaytimeOnly,
                    KidsAreaDescription: null,
                    HasPerformanceSpace: PerformanceSpaceStatus.No,
                    PerformanceTypes: null,
                    Vibes: [],
                    AdultPlayspace: AdultPlayspacePolicy.No,
                    MemberCount: 42,
                    SpaceRequirement: null,
                    SoundZone: null,
                    ContainerCount: 0,
                    ContainerNotes: null,
                    ElectricalGrid: null),
                historicalNames: null,
                year);

            camp = await _campService.GetCampBySlugAsync(created.Slug);
            var season = camp?.Seasons.FirstOrDefault(s => s.Year == year);
            if (season is not null)
            {
                await _campService.ApproveSeasonAsync(
                    season.Id,
                    leadUserId,
                    "Dev persona auto-seeded");
            }

            _logger.LogInformation("DEV: seeded camp {Slug} ({Id})", created.Slug, created.Id);
        }

        if (camp is null)
            return;

        if (!await _campService.IsUserCampLeadAsync(leadUserId, camp.Id))
        {
            await _campService.AddLeadAsync(camp.Id, leadUserId);
            _logger.LogInformation("DEV: seeded camp lead for {Slug} user {UserId}", campSlug, leadUserId);
        }
    }

    /// <summary>
    /// Ensures the coordinator persona's test department and sub-team exist with active memberships.
    /// </summary>
    public async Task EnsureCoordinatorTeamsAsync(Guid coordinatorUserId)
    {
        var now = _clock.GetCurrentInstant();
        var changed = false;
        var dept = await EnsureTeamAsync(
            name: "Dev Test Department",
            slug: "dev-test-department",
            description: "Test department for coordinator e2e tests",
            requiresApproval: true);
        var subTeam = await EnsureTeamAsync(
            name: "Dev Test SubTeam",
            slug: "dev-test-subteam",
            description: "Test sub-team for coordinator e2e tests",
            requiresApproval: true,
            parentTeamId: dept.Id);

        changed |= await EnsureSeededMembershipAsync(dept.Id, coordinatorUserId, TeamMemberRole.Coordinator, now);
        changed |= await EnsureSeededMembershipAsync(subTeam.Id, coordinatorUserId, TeamMemberRole.Member, now);

        if (changed)
        {
            _cache.InvalidateUserAccess(coordinatorUserId);
            // Team membership changes ripple into FullProfile (active-teams shape)
            // - InvalidateUserAccess only evicts ActiveTeams/role/shift caches,
            // not the FullProfile dict in CachingProfileService.
            await _fullProfileInvalidator.InvalidateAsync(coordinatorUserId);
            _logger.LogInformation(
                "DEV: ensured coordinator teams - department {DeptId}, sub-team {SubTeamId}",
                dept.Id, subTeam.Id);
        }
    }

    private async Task<Team> EnsureTeamAsync(
        string name,
        string slug,
        string description,
        bool requiresApproval,
        Guid? parentTeamId = null)
    {
        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is not null)
        {
            return team;
        }

        return await _teamService.CreateTeamAsync(
            name,
            description,
            requiresApproval,
            parentTeamId);
    }

    private async Task<bool> EnsureSeededMembershipAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole expectedRole,
        Instant now)
    {
        var existing = (await _teamService.GetUserTeamsAsync(userId))
            .FirstOrDefault(tm => tm.TeamId == teamId);

        if (existing is null)
        {
            await _teamService.AddSeededMemberAsync(teamId, userId, expectedRole, now);
            return true;
        }

        if (existing.Role != expectedRole)
        {
            await _teamService.SetMemberRoleAsync(teamId, userId, expectedRole, userId);
            return true;
        }
        return false;
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

        var now = _clock.GetCurrentInstant();
        var team = await EnsureTeamAsync(
            name: "City Planning",
            slug: teamSlug,
            description: "Dev-seeded city planning team",
            requiresApproval: false);

        var changed = await EnsureSeededMembershipAsync(
            team.Id,
            userId,
            TeamMemberRole.Coordinator,
            now);

        if (changed)
        {
            _cache.InvalidateUserAccess(userId);
            // Team membership changes ripple into FullProfile (active-teams shape)
            // - InvalidateUserAccess only evicts ActiveTeams/role/shift caches,
            // not the FullProfile dict in CachingProfileService.
            await _fullProfileInvalidator.InvalidateAsync(userId);
        }
    }

    /// <summary>
    /// Returns the first 100 users (display name ordered) for the dev user-chooser view.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string DisplayName, string Email)>> GetUsersForChooserAsync(
        CancellationToken ct = default)
    {
        var users = await _userService.GetAllUsersAsync(ct);
        return users
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select(u => (u.Id, u.DisplayName ?? u.Email ?? "Unknown", u.Email ?? string.Empty))
            .ToList();
    }

    public static bool IsBarrioLeadSlug(string slug) =>
        slug.EndsWith("-lead", StringComparison.OrdinalIgnoreCase) &&
        slug.StartsWith("barrio-", StringComparison.OrdinalIgnoreCase);

    public static bool IsCityPlanningSlug(string slug) =>
        string.Equals(slug, "city-planning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic GUID from persona slug - stable across restarts for idempotent seeding.
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
