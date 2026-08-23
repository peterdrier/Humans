using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Application.Services.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Users.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Data.Repositories;
using Humans.Users.Jobs;
using Humans.Users.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

/// <summary>
/// Users + Profiles' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <para>
/// Registrations are <c>UsersSectionExtensions.AddUsersSection</c> and
/// <c>ProfileSectionExtensions.AddProfileSection</c> merged verbatim, plus the
/// <c>AddSectionDbContext&lt;UsersDbContext&gt;</c> line moved out of
/// <c>InfrastructureServiceCollectionExtensions</c> (design §15 step 11).
/// </para>
/// <para>
/// <c>IDashboardService</c> and <c>IAdminDashboardService</c> are both gone entirely — the
/// member dashboard's content and the admin dashboard's tiles are section-contributed chrome
/// now (nobodies-collective/Humans#1091). The admin-dashboard aggregator's pieces scattered to
/// their owning sections: the tier-applications card to Governance, the preferred-language
/// card and audience-segmentation diagnostic stayed here (<c>IUsersAudienceService</c>), and
/// the Venn/UpSet set-membership card to Debug.
/// </para>
/// <para>
/// The <c>CachingUserService</c> block is copied as-is and is deliberately not tidied: one
/// Singleton resolved six ways, an <c>AddHostedService</c> for its startup warm, and the inner
/// <c>UserService</c> registered keyed with a Scoped unwrap so the decorator can open a scope
/// per call.
/// </para>
/// <para>
/// <c>ProcessAccountDeletionsJob</c> and <c>SuspendNonCompliantMembersJob</c> moved into this
/// project's <c>Contracts/</c> folder at G5 lane 5b-4 (nobodies-collective/Humans#866), and
/// their registration followed from Shell's <c>AdminSectionExtensions</c> at #1074's jobs
/// seam — their schedule is contributed via <c>SectionJobs.cs</c>.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Users carries the seven ASP.NET Identity tables; its sentinel is the chain-created
        // users table. Registered here rather than in AddHumansPersistence since #866 lane 2.
        services.AddSectionDbContext<UsersDbContext>(sentinelTable: "users");

        // Catches Identity-machinery writes (UpdateAsync, LastLoginAt, OAuth UserEmail).
        // Singleton, and resolved by AddSectionDbContext through IEnumerable<IInterceptor> onto
        // both the DbContext and the DbContextFactory pipelines of EVERY section — the
        // interceptor pattern-matches User/UserEmail/EventParticipation/CommunicationPreference
        // wherever they are saved. AddSectionDbContext named the concrete type until this lane;
        // it resolves sp.GetServices<IInterceptor>() now, so a section owns its own
        // interceptors and Base names none of them.
        services.AddSingleton<UserInfoSaveChangesInterceptor>();
        services.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<UserInfoSaveChangesInterceptor>());

        // User section — see #511 / #703. CachingUserService decorator + UserInfo read-model spans User/Profile sections.
        services.AddSingleton<IUserRepository, UserRepository>();

        // Account merge + duplicate detection — moved Profiles → Users (PR 899): the
        // AccountMergeRequests table is a Users table, so its repository + services live here.
        // AccountMergeService also contributes to the GDPR export fan-out (IUserDataContributor).
        services.AddSingleton<IAccountMergeRepository, AccountMergeRepository>();
        services.AddScoped<AccountMergeService>();
        services.AddScoped<IAccountMergeService>(sp => sp.GetRequiredService<AccountMergeService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AccountMergeService>());
        services.AddScoped<IDuplicateAccountService, DuplicateAccountService>();

        // Inner Scoped + keyed; decorator resolves via IServiceScopeFactory per-call.
        services.AddKeyedScoped<IUserService, UserService>(CachingUserService.InnerServiceKey);
        services.AddScoped<UserService>(sp =>
            (UserService)sp.GetRequiredKeyedService<IUserService>(CachingUserService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<UserService>());

        // Singleton so _byUserId dict survives across requests.
        services.AddSingleton<CachingUserService>();
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserServiceRead>(sp => sp.GetRequiredService<CachingUserService>());
        // Guid → display-name fan-out (nobodies-collective/Humans#1059).
        services.AddSingleton<IEntityNameContributor>(sp => sp.GetRequiredService<CachingUserService>());

        // Same Singleton instance must back invalidator + merge so external "user changed" signals hit the cache owner.
        services.AddSingleton<IUserInfoInvalidator>(sp =>
            sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserMerge>(sp =>
            sp.GetRequiredService<CachingUserService>());

        // Section-internal slice-refresh signal consumed by UserInfoSaveChangesInterceptor.
        // Not part of the cross-section §15e contract; same Singleton instance as IUserInfoInvalidator.
        services.AddSingleton<IUserInfoSliceRefresher>(sp =>
            sp.GetRequiredService<CachingUserService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingUserService>());

        // Hosted service for TrackedCache StartAsync → WarmAllAsync.
        services.AddHostedService(sp => sp.GetRequiredService<CachingUserService>());

        // Profile section — see #504. Singleton repos so CachingUserService injects directly without scope-factory.
        services.AddSingleton<ICommunicationPreferenceRepository, CommunicationPreferenceRepository>();

        services.AddScoped<IUnsubscribeTokenProvider, UnsubscribeTokenProvider>();

        services.AddScoped<CommunicationPreferenceService>();
        services.AddScoped<ICommunicationPreferenceService>(sp => sp.GetRequiredService<CommunicationPreferenceService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CommunicationPreferenceService>());

        services.AddScoped<IUnsubscribeService, UnsubscribeService>();

        services.AddScoped<ContactFieldService>();
        services.AddScoped<IContactFieldService>(sp => sp.GetRequiredService<ContactFieldService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ContactFieldService>());

        services.AddScoped<UserEmailService>();
        services.AddScoped<IUserEmailService>(sp => sp.GetRequiredService<UserEmailService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<UserEmailService>());

        services.AddScoped<IEmailProblemsService, EmailProblemsService>();
        services.AddScoped<IUserNameSyncService, UserNameSyncService>();
        services.AddScoped<IProfileEditorService, ProfileEditorService>();
        services.AddScoped<IAccountProvisioningService, AccountProvisioningService>();

        // Resource-based authorization handler for UserEmail operations. The *policy* stays in
        // Shell's AuthorizationPolicyExtensions; the handler moves in with the section
        // (design §15 step 6's asymmetry).
        services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();

        // Backs PolicyNames.HumanAdminOnly (registered in this section's Policies).
        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();

        // FullProfile cache retired — denormalized reads go through IUserService.GetUserInfoAsync.
        services.AddScoped<ProfileService>();
        services.AddScoped<IProfilePictureService>(sp => sp.GetRequiredService<ProfileService>());

        // The suspend/unsuspend state machine over this section's own IUserService, and the body
        // of the nightly non-compliance sweep. Both arrived at G5 lane 4b-2d from Base (Peter,
        // 2026-08-14: membership lifecycle is Users, not Governance). Both are orchestrators by
        // the hard rules' definition — they inject no I*Repository — but they orchestrate *this*
        // section, and every outbound edge is another section's leaf.
        services.AddScoped<IHumanLifecycleService, HumanLifecycleService>();
        services.AddScoped<INonCompliantMemberSuspension, NonCompliantMemberSuspension>();

        // The last three Users services to leave Base, at G5 lane 5c. Same shape as the pair
        // above: no repository, so orchestrators by the hard rules' definition, but what they
        // orchestrate is this section and every outbound edge is another section's leaf.
        // ExternalLoginService keeps its Humans.Application.Services.Users namespace —
        // HUM0005 names it as the sole legal caller of ReconcileOAuthIdentityAsync by full name.
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        services.AddScoped<IExternalLoginService, ExternalLoginService>();
        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();

        // Audience-segmentation diagnostic for UsersAdminController.Audience — split off the
        // deleted admin-dashboard aggregator at nobodies-collective/Humans#1091.
        services.AddScoped<IUsersAudienceService, UsersAudienceService>();
    }
}
