using Humans.Base.Extensions;
using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Cantina.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Data.Repositories;
using Humans.Users.Jobs;
using Humans.Users.Models;
using Humans.Users.Services;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;
using Humans.AuditLog.Contracts;

[assembly: Humans.Base.Demo.NoDemoStory("The demo cast is created through Users by Development's cast story.")]

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
    ISectionMemberDashboard,
    ISectionPolicies,
    ISectionThingsToDo,
    ISearchContributor,
    IUserPart
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(UsersRoles.UsersAdmin, "Users Admin",
            "Keepers of the human directory. They manage profiles, handle role assignments, and provision workspace accounts. If you exist in this system, they probably helped.",
            "fa-solid fa-users-gear",
            FormerNames: ["HumanAdmin"]),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, UsersAuditActions>();
        services.AddSingleton<INotificationSourceContributor, UsersNotificationSources>();
        services.AddScoped<INotificationMeterContributor, UsersNotificationMeters>();
        services.AddSectionDbContext<UsersDbContext>(sentinelTable: "users");

        services.AddIdentity<User, IdentityRole<Guid>>(options =>
            {
                // UserEmail table owns email uniqueness; User.Email is null on new users.
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddEntityFrameworkStores<UsersDbContext>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<HumansUserClaimsPrincipalFactory>();

        // On every section's context pipeline: catches User/UserEmail
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

        services.AddScoped<ProfileService>();
        services.AddScoped<MedicalConditionsService>();
        services.AddScoped<EmailConfirmationService>();
        services.AddScoped<MergeAccountCardBuilder>();
        services.AddScoped<IProfilePictureService>(sp => sp.GetRequiredService<ProfileService>());

        services.AddScoped<IHumanLifecycleService, HumanLifecycleService>();
        services.AddScoped<INonCompliantMemberSuspension, NonCompliantMemberSuspension>();

        services.AddScoped<AccountDeletionService>();
        services.AddScoped<IAccountDeletionService>(sp => sp.GetRequiredService<AccountDeletionService>());
        services.AddScoped<IExternalLoginService, ExternalLoginService>();
        services.AddScoped<IUserSignInService, UserSignInService>();

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
        },
        new()
        {
            Key = "UsersAdmin",
            SectionName = "All Humans",
            Order = 90,
            Roles = ["Board", "UsersAdmin", "Admin"],
            Features =
            [
                Of("All humans list", ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Users", [
            new("Humans", "UsersAdmin", "AdminList", null, null, "fa-solid fa-users",    UsersPolicies.Read, Weight: 0),
            new("Account merges", "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly, Weight: 30),
            new("Email problems", "ProfileAdmin", "EmailProblems", null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly, Weight: 40),
            new("Audience segmentation", "UsersAdmin", "Audience", null, null, "fa-solid fa-chart-pie", PolicyNames.AdminOnly, Weight: 50)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("users.total", "Total users", "fa-solid fa-users", TotalAsync, Weight: 10),
        new AdminTile("users.profiles", "Active (has profile)", "fa-solid fa-id-card", ProfilesAsync, Weight: 20)
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

    private static async Task<IReadOnlyCollection<UserInfo>> SnapshotAsync(IServiceProvider sp, CancellationToken ct) =>
        await sp.GetRequiredService<IUserService>().GetAllUserInfosAsync(ct);

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(PreferredLanguageCardViewComponent), Weight: 40),
            new(ChromeSlots.WidgetGallery, typeof(UsersGalleryViewComponent)),
        ];

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
        [new(ChromeSlots.MemberDashboard, typeof(HumansDirectoryCardsViewComponent), Weight: 90)];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetRequiredSetting(registry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
    }

    private const string SectionName = "Users";

    internal const string LegacyIdentityEmailsKey = "users.legacy-identity-emails";

    internal const string LeftoverMergeRequestsKey = "users.leftover-merge-requests";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(LegacyIdentityEmailsKey, SectionName,
            "Create a verified UserEmail row from the legacy User.Email Identity column",
            "The legacy-column fallback in User.Email / UserInfo.Email",
            LegacyEmailsStatusAsync, ApplyLegacyEmailsAsync),
        new(LeftoverMergeRequestsKey, SectionName,
            "Dismiss merge requests left from before merges were self-service: removes each pending email, the accounts are untouched",
            "The leftover-request listing and dismiss code in AccountMergeService",
            LeftoverMergeRequestsStatusAsync, ApplyLeftoverMergeRequestsAsync),
    ];

    private static async Task<DataMigrationStatus> LeftoverMergeRequestsStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Address", "Asked by", "Already on", "Asked"],
            [.. (await sp.GetRequiredService<IAccountMergeService>().GetPendingRequestsAsync(ct))
                .Select(r => (IReadOnlyList<string>)
                [
                    r.Email, r.TargetUser.Id.ToString(), r.SourceUser.Id.ToString(),
                    r.CreatedAt.ToInvariantTimestamp(),
                ])]);

    private static async Task<int> ApplyLeftoverMergeRequestsAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct)
    {
        var merges = sp.GetRequiredService<IAccountMergeService>();
        var dismissed = 0;
        foreach (var request in await merges.GetPendingRequestsAsync(ct))
        {
            var result = await merges.RejectAsync(
                request.Id, actorUserId, "Dismissed: merges are self-service now.", ct);
            if (result.Refusal is null) dismissed++;
        }
        return dismissed;
    }

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
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "UsersAdmin", 81),
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
        options.AddPolicy(UsersPolicies.Read, policy =>
            policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(UsersPolicies.Manage, policy =>
            policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Admin));

        options.AddPolicy(UsersPolicies.MedicalDataViewer, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.Board));
    }

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.GetUserId() is not { } userId)
        {
            return [];
        }

        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var logger = services.GetRequiredService<ILogger<Section>>();
        var info = await services.GetRequiredService<IUserService>().GetUserInfoAsync(userId, ct);

        var entries = new List<ThingsToDoEntry> { await ProfileEntryAsync(services, localizer, info, userId, ct) };

        var medical = await MedicalEntryAsync(services, localizer, logger, userId, ct);
        if (medical is not null)
        {
            entries.Add(medical);
        }

        return entries;
    }

    private static async ValueTask<ThingsToDoEntry> ProfileEntryAsync(
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        UserInfo? info,
        Guid userId, CancellationToken ct)
    {
        var shiftUser = await services.GetRequiredService<IShiftView>().GetUserAsync(userId, ct);
        var percent = ProfileCompletion.ComputePercent(info, shiftUser.TagPreferences.Count > 0);

        // Hidden or derived required fields can cap real completion around 90–95%, so 80% is
        // "complete enough" and the nudge stops shouting at people who can't push it higher.
        var complete = percent >= 80;

        return new ThingsToDoEntry("profile", localizer["Todo_Profile_Title"].Value, "fa-solid fa-user",
            Controller: "Profile", Action: "Edit", Weight: 10)
        {
            Description = complete
                ? localizer["Todo_Profile_Done"].Value
                : localizer["Dashboard_ProfileCompletionPercent", percent],
            IsDone = complete,
            ActionText = localizer["Todo_Profile_Action"].Value,
            PercentComplete = complete ? null : percent,
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
        Guid userId, CancellationToken ct)
    {
        try
        {
            var dietary = await services.GetRequiredService<ICantinaService>().GetDietaryProfileAsync(userId, ct);
            if (dietary is { HasPreference: true } or { DeclinedToAnswer: true })
            {
                return null;
            }

            var medical = await services.GetRequiredService<MedicalConditionsService>().GetOwnAsync(userId, ct);
            if (!string.IsNullOrEmpty(medical))
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
        new(UserPartSlots.ConsentReviewApplicant, typeof(ProfileCardViewComponent)),
    ];

    IEnumerable<SearchCategory> ISearchContributor.Categories() =>
    [
        new("Human",
            sp => sp.GetRequiredService<IStringLocalizer<UsersResource>>()["Users_Search_Category"].Value,
            "fa-solid fa-user", typeof(UserSearchResultViewComponent), SearchHumansAsync, Weight: 0),
    ];

    // Public surface only: admin fields never reach /Search, whatever the viewer's role.
    private static async Task<IReadOnlyList<SearchHit>> SearchHumansAsync(
        IServiceProvider sp, string query, CancellationToken ct) =>
        [.. (await sp.GetRequiredService<IUserService>().SearchUsersAsync(query, PersonSearchFields.PublicAll, int.MaxValue, ct))
            .Select(h => new SearchHit(h.UserId, h.BurnerName, h.Score, h.MatchField, h.MatchSnippet))];
}
