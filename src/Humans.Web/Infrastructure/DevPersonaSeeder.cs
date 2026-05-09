using System.Security.Cryptography;
using System.Text;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Configuration;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
/// <see cref="IUserEmailService"/>). Auxiliary dev fixtures (system-team
/// memberships, dev test department, dev barrio camp/season/lead, city-planning
/// team, role assignments, sample contact fields) are written directly via
/// <see cref="HumansDbContext"/> because there is no existing service surface
/// for "create a fresh Team / Camp / RoleAssignment row from scratch with a
/// deterministic id" — the same shape as
/// <see cref="DevelopmentDashboardSeeder"/>'s direct-write to Shifts-owned
/// tables.
/// </summary>
public sealed class DevPersonaSeeder
{
    private readonly UserManager<User> _userManager;
    private readonly HumansDbContext _db;
    private readonly IProfileService _profileService;
    private readonly IUserEmailService _userEmailService;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly IOptions<CityPlanningOptions> _cityPlanningOptions;
    private readonly ILogger<DevPersonaSeeder> _logger;

    public DevPersonaSeeder(
        UserManager<User> userManager,
        HumansDbContext db,
        IProfileService profileService,
        IUserEmailService userEmailService,
        IFullProfileInvalidator fullProfileInvalidator,
        IClock clock,
        IMemoryCache cache,
        IOptions<CityPlanningOptions> cityPlanningOptions,
        ILogger<DevPersonaSeeder> logger)
    {
        _userManager = userManager;
        _db = db;
        _profileService = profileService;
        _userEmailService = userEmailService;
        _fullProfileInvalidator = fullProfileInvalidator;
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

        // Legacy personas may exist with old hardcoded GUIDs — reuse them.
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
            RemoveProfilePicture: false,
            SelectedTier: null,
            ApplicationMotivation: null,
            ApplicationAdditionalInfo: null,
            ApplicationSignificantContribution: null,
            ApplicationRoleUnderstanding: null);

        var profileId = await _profileService.SaveProfileAsync(id, displayName, saveRequest, "en");

        // Mark approved + cleared so the dev persona skips the consent gate
        // and lands on the dashboard. Routes through ProfileService so the
        // CachingProfileService decorator handles the FullProfile cache
        // refresh atomically with the DB write (issue #474 — Profiles is the
        // single writer to the profile state fields).
        var consentCheckResult = await _profileService.RecordConsentCheckAsync(
            id, reviewerId: id, ConsentCheckStatus.Cleared,
            notes: "Dev persona — auto-seeded");
        if (!consentCheckResult.Success)
            _logger.LogWarning(
                "DEV: consent-check approval failed for persona {UserId}: {ErrorKey}",
                id, consentCheckResult.ErrorKey);

        // Seed sample contact fields so profile page exercises the contact rendering path.
        // ContactFields are Profile-section auxiliary data with no public service write
        // surface for "seed deterministic dev rows" — direct DbContext writes mirror the
        // DevelopmentDashboardSeeder pattern.
        _db.ContactFields.Add(new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            FieldType = ContactFieldType.Signal,
            Value = $"+34 600 000 {id.ToString()[..3]}",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        });
        _db.ContactFields.Add(new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            FieldType = ContactFieldType.Telegram,
            Value = $"@dev_{slug}",
            Visibility = ContactFieldVisibility.MyTeams,
            DisplayOrder = 1,
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var teamId in teams)
        {
            _db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                UserId = id,
                TeamId = teamId,
                Role = TeamMemberRole.Member,
                JoinedAt = now
            });
        }

        foreach (var role in roles)
        {
            _db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(),
                UserId = id,
                RoleName = role,
                ValidFrom = now,
                ValidTo = null,
                Notes = "Dev persona — auto-seeded",
                CreatedAt = now,
                CreatedByUserId = id
            });
        }

        await _db.SaveChangesAsync();
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
    /// Seeds a profileless user — just User + UserEmail, no Profile, no teams, no roles.
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

        var year = (await _db.CampSettings.FirstAsync()).PublicYear;

        var campId = PersonaGuid($"dev-camp:{campSlug}");
        var seasonId = PersonaGuid($"dev-camp-season:{campSlug}:{year}");
        var leadId = PersonaGuid($"dev-camp-lead:{campSlug}:{leadUserId}");

        var now = _clock.GetCurrentInstant();

        if (!await _db.Set<Camp>().AnyAsync(c => c.Id == campId))
        {
            _db.Set<Camp>().Add(new Camp
            {
                Id = campId,
                Slug = campSlug,
                ContactEmail = $"dev-{campSlug}@localhost",
                ContactPhone = "+34 600 000 000",
                CreatedByUserId = leadUserId,
                CreatedAt = now,
                UpdatedAt = now
            });

            _db.Set<CampSeason>().Add(new CampSeason
            {
                Id = seasonId,
                CampId = campId,
                Year = year,
                Name = campName,
                Status = CampSeasonStatus.Active,
                BlurbShort = $"A dev test barrio ({campName}).",
                BlurbLong = $"{campName} is a development test barrio used for local and preview environment testing. Feel free to edit this description.",
                Languages = "English, Spanish",
                MemberCount = 42,
                CreatedAt = now,
                UpdatedAt = now
            });

            _logger.LogInformation("DEV: seeded camp {Slug} ({Id})", campSlug, campId);
        }

        if (!await _db.Set<CampLead>().AnyAsync(l => l.Id == leadId))
        {
            _db.Set<CampLead>().Add(new CampLead
            {
                Id = leadId,
                CampId = campId,
                UserId = leadUserId,
                Role = CampLeadRole.Primary,
                JoinedAt = now
            });

            _logger.LogInformation("DEV: seeded camp lead for {Slug} user {UserId}", campSlug, leadUserId);
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Ensures the coordinator persona's test department and sub-team exist with active memberships.
    /// </summary>
    public async Task EnsureCoordinatorTeamsAsync(Guid coordinatorUserId)
    {
        var now = _clock.GetCurrentInstant();
        var changed = false;
        var deptId = PersonaGuid("dev-test-department");
        var subTeamId = PersonaGuid("dev-test-subteam");

        if (!await _db.Teams.AnyAsync(t => t.Id == deptId))
        {
            _db.Teams.Add(new Team
            {
                Id = deptId,
                Name = "Dev Test Department",
                Description = "Test department for coordinator e2e tests",
                Slug = "dev-test-department",
                IsActive = true,
                RequiresApproval = true,
                SystemTeamType = SystemTeamType.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            changed = true;
        }

        if (!await _db.Teams.AnyAsync(t => t.Id == subTeamId))
        {
            _db.Teams.Add(new Team
            {
                Id = subTeamId,
                Name = "Dev Test SubTeam",
                Description = "Test sub-team for coordinator e2e tests",
                Slug = "dev-test-subteam",
                IsActive = true,
                RequiresApproval = true,
                SystemTeamType = SystemTeamType.None,
                ParentTeamId = deptId,
                CreatedAt = now,
                UpdatedAt = now
            });
            changed = true;
        }

        changed |= await EnsureSeededMembershipAsync(deptId, coordinatorUserId, TeamMemberRole.Coordinator, now);
        changed |= await EnsureSeededMembershipAsync(subTeamId, coordinatorUserId, TeamMemberRole.Member, now);

        if (changed)
        {
            await _db.SaveChangesAsync();
            _cache.InvalidateActiveTeams();
            _cache.InvalidateUserAccess(coordinatorUserId);
            // Team membership changes ripple into FullProfile (active-teams shape)
            // — InvalidateUserAccess only evicts ActiveTeams/role/shift caches,
            // not the FullProfile dict in CachingProfileService.
            await _fullProfileInvalidator.InvalidateAsync(coordinatorUserId);
            _logger.LogInformation(
                "DEV: ensured coordinator teams — department {DeptId}, sub-team {SubTeamId}",
                deptId, subTeamId);
        }
    }

    private async Task<bool> EnsureSeededMembershipAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole expectedRole,
        Instant now)
    {
        var existing = await _db.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId);

        if (existing is null)
        {
            _db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TeamId = teamId,
                Role = expectedRole,
                JoinedAt = now
            });
            return true;
        }

        var changed = false;
        if (existing.LeftAt is not null)
        {
            existing.LeftAt = null;
            changed = true;
        }
        if (existing.Role != expectedRole)
        {
            existing.Role = expectedRole;
            changed = true;
        }
        return changed;
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
        var changed = false;

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Slug == teamSlug);
        if (team is null)
        {
            team = new Team
            {
                Id = Guid.NewGuid(),
                Name = "City Planning",
                Description = "Dev-seeded city planning team",
                Slug = teamSlug,
                IsActive = true,
                RequiresApproval = false,
                SystemTeamType = SystemTeamType.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Teams.Add(team);
            changed = true;
            _logger.LogInformation("DEV: seeded city planning team {Slug}", teamSlug);
        }

        var hasActiveMembership = await _db.TeamMembers
            .AnyAsync(tm => tm.TeamId == team.Id && tm.UserId == userId && tm.LeftAt == null);
        if (!hasActiveMembership)
        {
            var inactiveMembership = await _db.TeamMembers
                .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == userId && tm.LeftAt != null);
            if (inactiveMembership is not null)
            {
                inactiveMembership.LeftAt = null;
                inactiveMembership.Role = TeamMemberRole.Coordinator;
            }
            else
            {
                _db.TeamMembers.Add(new TeamMember
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TeamId = team.Id,
                    Role = TeamMemberRole.Coordinator,
                    JoinedAt = now
                });
            }
            changed = true;
        }

        if (changed)
        {
            await _db.SaveChangesAsync();
            _cache.InvalidateActiveTeams();
            _cache.InvalidateUserAccess(userId);
            // Team membership changes ripple into FullProfile (active-teams shape)
            // — InvalidateUserAccess only evicts ActiveTeams/role/shift caches,
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
        var users = await _db.Users
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .Take(100)
            .ToListAsync(ct);

        return users
            .Select(u => (u.Id, u.DisplayName ?? u.Email ?? "Unknown", u.Email ?? string.Empty))
            .ToList();
    }

    public static bool IsBarrioLeadSlug(string slug) =>
        slug.EndsWith("-lead", StringComparison.OrdinalIgnoreCase) &&
        slug.StartsWith("barrio-", StringComparison.OrdinalIgnoreCase);

    public static bool IsCityPlanningSlug(string slug) =>
        string.Equals(slug, "city-planning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic GUID from persona slug — stable across restarts for idempotent seeding.
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
