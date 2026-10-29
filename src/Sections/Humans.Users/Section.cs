using System.Globalization;
using System.Security.Claims;
using Humans.Application.Services.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Cantina.Contracts;
using Humans.Consent.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Issues.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Data.Repositories;
using Humans.Users.Jobs;
using Humans.Users.Services;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Users;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionChrome,
    ISectionConfiguration,
    ISectionDataMigrations,
    ISectionHelp,
    ISectionJobs,
    ISectionPolicies,
    ISectionThingsToDo,
    IUserPart
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(UsersRoles.UsersAdmin, "Users Admin",
            "Keepers of the human directory. They manage profiles, handle role assignments, and provision workspace accounts. If you exist in this system, they probably helped.",
            "fa-solid fa-users-gear",
            FormerNames: ["HumanAdmin"]),
    ];

    internal static IdentityBuilder AddIdentityStores(IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<UsersDbContext>();

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<UsersDbContext>(sentinelTable: "users");

        // On every section's context pipeline: catches User/UserEmail/CommunicationPreference
        // writes wherever they are saved, including Identity machinery.
        services.AddSingleton<UserInfoSaveChangesInterceptor>();
        services.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<UserInfoSaveChangesInterceptor>());

        services.AddSingleton<IUserRepository, UserRepository>();

        services.AddSingleton<IAccountMergeRepository, AccountMergeRepository>();
        services.AddScoped<AccountMergeService>();
        services.AddScoped<IAccountMergeService>(sp => sp.GetRequiredService<AccountMergeService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AccountMergeService>());
        services.AddScoped<IDuplicateAccountService, DuplicateAccountService>();

        services.AddKeyedScoped<IUserManagement, UserService>(CachingUserService.InnerServiceKey);
        services.AddScoped<UserService>(sp =>
            (UserService)sp.GetRequiredKeyedService<IUserManagement>(CachingUserService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<UserService>());

        services.AddSingleton<CachingUserService>();
        services.AddSingleton<IUserManagement>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserWrite>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IEntityNameContributor>(sp => sp.GetRequiredService<CachingUserService>());

        // Same Singleton instance must back merge so the cache owner sees it.
        services.AddSingleton<IUserMerge>(sp =>
            sp.GetRequiredService<CachingUserService>());

        services.AddSingleton<IUserInfoSliceRefresher>(sp =>
            sp.GetRequiredService<CachingUserService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingUserService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingUserService>());

        services.AddSingleton<ICommunicationPreferenceRepository, CommunicationPreferenceRepository>();

        services.AddScoped<ContactFieldService>();
        services.AddScoped<IContactFieldService>(sp => sp.GetRequiredService<ContactFieldService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ContactFieldService>());

        services.AddScoped<UserEmailService>();
        services.AddScoped<IUserEmailService>(sp => sp.GetRequiredService<UserEmailService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<UserEmailService>());

        services.AddScoped<UsersEmails>();
        services.AddScoped<IEmailPreviewContributor, UsersEmailPreviews>();

        services.AddScoped<IEmailProblemsService, EmailProblemsService>();
        services.AddScoped<IProfileEditorService, ProfileEditorService>();
        services.AddScoped<IAccountProvisioningService, AccountProvisioningService>();

        services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();

        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();

        services.AddScoped<ProfileService>();
        services.AddScoped<IProfilePictureService>(sp => sp.GetRequiredService<ProfileService>());

        services.AddScoped<IHumanLifecycleService, HumanLifecycleService>();
        services.AddScoped<INonCompliantMemberSuspension, NonCompliantMemberSuspension>();

        // ExternalLoginService keeps its Humans.Application.Services.Users namespace: HUM0005
        // names it by full name as the sole caller of ReconcileOAuthIdentityAsync.
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        services.AddScoped<IExternalLoginService, ExternalLoginService>();

        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();

        services.AddScoped<IUsersAudienceService, UsersAudienceService>();
    }

    // "Profiles", not "Users": stored rows still carry the merged-in section's old key.
    string IIssueQueueOwner.QueueKey => "Profiles";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Profiles";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["profile", "humans"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [UsersRoles.UsersAdmin];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Profile",
            SectionName = "Profile",
            Order = 80,
            Roles = ["Volunteer", "Coordinator", "Board", "UsersAdmin", "Admin"],
            Features =
            [
                Of("View own profile", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("Edit own profile", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("View other profiles", ("Volunteer", Limited), ("Coordinator", Allowed), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("View contact fields", ("Volunteer", Limited), ("Coordinator", Limited), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("Admin view of profile", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Users", [
            new("Humans", "UsersAdmin", "AdminList", null, null, "fa-solid fa-users",    PolicyNames.HumanAdminBoardOrAdmin, Weight: 0),
            new("Account merges", "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly, Weight: 30),
            new("Email problems", "ProfileAdmin", "EmailProblems", null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly, Weight: 40),
            new("Audience segmentation", "UsersAdmin", "Audience", null, null, "fa-solid fa-chart-pie", PolicyNames.AdminOnly, Weight: 50)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("users.total", "Total users", "fa-solid fa-users", TotalAsync, Weight: 10),
        new AdminTile("users.profiles", "Active (has profile)", "fa-solid fa-id-card", ProfilesAsync, Weight: 20),
        new AdminTile("users.tickets", "Ticket holders", "fa-solid fa-ticket", TicketHoldersAsync, Weight: 30)
    ];

    private static async ValueTask<AdminTileValue?> TotalAsync(IServiceProvider sp, CancellationToken ct)
    {
        var count = (await SnapshotAsync(sp, ct)).Count;
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} users");
    }

    private static async ValueTask<AdminTileValue?> ProfilesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var count = (await SnapshotAsync(sp, ct)).Count(u => u.IsActive);
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} with profile");
    }

    private static async ValueTask<AdminTileValue?> TicketHoldersAsync(IServiceProvider sp, CancellationToken ct)
    {
        var activeEvent = await sp.GetRequiredService<ISettingsService>().GetActiveEventSettingsAsync(ct);
        var count = 0;
        if (activeEvent is { Year: > 0 })
        {
            var living = (await SnapshotAsync(sp, ct)).Select(u => u.Id).ToHashSet();
            count = (await sp.GetRequiredService<ITicketService>().GetAllParticipationsAsync(ct))
                .Count(p => p.Year == activeEvent.Year && p.HasTicket && living.Contains(p.UserId));
        }
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} with ticket");
    }

    private static async Task<IReadOnlyCollection<UserInfo>> SnapshotAsync(IServiceProvider sp, CancellationToken ct) =>
        await sp.GetRequiredService<IUserService>().GetAllUserInfosAsync(ct);

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(PreferredLanguageCardViewComponent), Weight: 40),
            new(ChromeSlots.WidgetGallery, typeof(UsersGalleryViewComponent)),
        ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetRequiredSetting(registry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
    }

    private const string SectionName = "Users";

    internal const string LegacyIdentityEmailsKey = "users.legacy-identity-emails";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(LegacyIdentityEmailsKey, SectionName,
            "Create a verified UserEmail row from the legacy User.Email Identity column",
            "The legacy-column fallback in User.Email / UserInfo.Email",
            LegacyEmailsStatusAsync, ApplyLegacyEmailsAsync),
    ];

    private static async Task<DataMigrationStatus> LegacyEmailsStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Legacy User.Email"],
            [.. (await sp.GetRequiredService<IEmailProblemsService>().ScanAsync(ct)).Problems
                .Where(p => p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails && p.UserId is not null)
                .Select(p => (IReadOnlyList<string>)
                [
                    p.UserId!.Value.ToString(), p.Email ?? string.Empty,
                ])]);

    private static async Task<int> ApplyLegacyEmailsAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        (await sp.GetRequiredService<IEmailProblemsService>().BackfillLegacyIdentityEmailsAsync(actorUserId, ct)).Count;

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Profile", 20),
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "users-account-deletions", typeof(ProcessAccountDeletionsJob), "0 0 * * *");

        yield return new RecurringJobDescriptor(
            "users-suspend-non-compliant", typeof(SuspendNonCompliantMembersJob), "30 4 * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.HumanAdminBoardOrAdmin, policy =>
            policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(PolicyNames.HumanAdminOrAdmin, policy =>
            policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Admin));

        options.AddPolicy(PolicyNames.HumanAdminOnly, policy =>
            policy.AddRequirements(new HumanAdminOnlyRequirement()));
    }

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return [];
        }

        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var logger = services.GetRequiredService<ILogger<Section>>();
        var profile = (await services.GetRequiredService<IUserService>().GetUserInfoAsync(userId, ct))?.Profile;
        var isVolunteerMember = (await services.GetRequiredService<IMembershipCalculator>()
            .GetMembershipSnapshotAsync(userId, ct)).IsVolunteerMember;

        var entries = new List<ThingsToDoEntry> { await ProfileEntryAsync(services, localizer, profile, userId, ct) };

        if (!isVolunteerMember)
        {
            var review = await services.GetRequiredService<IConsentService>().GetConsentReviewAsync(userId, ct);
            entries.Add(ConsentCheckEntry(localizer, profile is not null && review.Status == ConsentCheckStatus.Cleared));
        }

        var medical = await MedicalEntryAsync(services, localizer, logger, profile, userId, ct);
        if (medical is not null)
        {
            entries.Add(medical);
        }

        return entries;
    }

    private static async ValueTask<ThingsToDoEntry> ProfileEntryAsync(
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        ProfileInfo? profile,
        Guid userId, CancellationToken ct)
    {
        var shiftUser = await services.GetRequiredService<IShiftView>().GetUserAsync(userId, ct);
        var percent = ProfileCompletion.ComputePercent(profile, shiftUser.TagPreferences.Count > 0);

        // Hidden or derived required fields can cap real completion around 90–95%, so 80% is
        // "complete enough" and the nudge stops shouting at people who can't push it higher.
        var complete = percent >= 80;

        return new ThingsToDoEntry("profile", localizer["Todo_Profile_Title"].Value, "fa-solid fa-user",
            Controller: "Profile", Action: "Edit", Weight: 10)
        {
            Description = complete
                ? localizer["Todo_Profile_Done"].Value
                : string.Format(CultureInfo.CurrentCulture,
                    localizer["Dashboard_ProfileCompletionPercent"].Value, percent),
            IsDone = complete,
            ActionText = localizer["Todo_Profile_Action"].Value,
            PercentComplete = complete ? null : percent,
        };
    }

    private static ThingsToDoEntry ConsentCheckEntry(IStringLocalizer<SharedResource> localizer, bool cleared)
    {
        return new ThingsToDoEntry("consent-check", localizer["Todo_ConsentCheck_Title"].Value,
            "fa-solid fa-clipboard-check", Weight: 30)
        {
            Description = cleared
                ? localizer["Todo_ConsentCheck_Done"].Value
                : localizer["Todo_ConsentCheck_Pending"].Value,
            IsDone = cleared,
        };
    }

    /// <summary>
    /// Shown while the dietary nudge is open and no medical conditions are on file. MedicalConditions
    /// has no "answered" marker, so tying it to the dietary answer keeps it from nagging forever.
    /// </summary>
    private static async ValueTask<ThingsToDoEntry?> MedicalEntryAsync(
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        ILogger logger,
        ProfileInfo? profile,
        Guid userId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(profile?.MedicalConditions))
        {
            return null;
        }

        try
        {
            var dietary = await services.GetRequiredService<ICantinaService>().GetDietaryProfileAsync(userId, ct);
            if (dietary is { HasPreference: true } or { DeclinedToAnswer: true })
            {
                return null;
            }

            return new ThingsToDoEntry("medical", localizer["Todo_Medical_Title"].Value,
                "fa-solid fa-notes-medical", Controller: "Profile", Action: "Medical", Weight: 51)
            {
                Description = localizer["Todo_Medical_Pending"].Value,
                ActionText = localizer["Todo_DietaryMedical_Action"].Value,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to check the medical prompt for user {UserId}", userId);
            return null;
        }
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.BoardVoteApplicant, typeof(ProfileCardViewComponent)),
        new(UserPartSlots.OnboardingReviewApplicant, typeof(ProfileCardViewComponent)),
        new(UserPartSlots.TicketTransferParty, typeof(HumanSummaryViewComponent)),
    ];
}
