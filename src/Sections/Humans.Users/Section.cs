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
/// The <c>CachingUserService</c> registration block: one Singleton resolved six ways, an
/// <c>AddHostedService</c> for its startup warm, and the inner <c>UserService</c> registered
/// keyed-scoped so the decorator can open a scope per call.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Users carries the seven ASP.NET Identity tables; its sentinel is the chain-created
        // users table.
        services.AddSectionDbContext<UsersDbContext>(sentinelTable: "users");

        // Catches Identity-machinery writes (UpdateAsync, LastLoginAt, OAuth UserEmail).
        // Singleton, resolved onto every section's DbContext and DbContextFactory pipelines via
        // IEnumerable<IInterceptor> — the interceptor pattern-matches
        // User/UserEmail/EventParticipation/CommunicationPreference wherever they are saved.
        // A section owns its own interceptors; Base names none of them.
        services.AddSingleton<UserInfoSaveChangesInterceptor>();
        services.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<UserInfoSaveChangesInterceptor>());

        // CachingUserService decorator + UserInfo read-model span the User and Profile tables.
        services.AddSingleton<IUserRepository, UserRepository>();

        // AccountMergeRequests is a Users table, so its repository and services live here;
        // AccountMergeService also feeds the GDPR export fan-out.
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
        // Guid → display-name fan-out.
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

        // Singleton repos so CachingUserService injects directly without a scope factory.
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

        // Resource-based authorization handler for UserEmail operations. Policy stays in
        // Shell's AuthorizationPolicyExtensions; the handler lives with the section.
        services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();

        // Backs PolicyNames.HumanAdminOnly (registered in this section's Policies).
        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();

        services.AddScoped<ProfileService>();
        services.AddScoped<IProfilePictureService>(sp => sp.GetRequiredService<ProfileService>());

        // The suspend/unsuspend state machine over this section's own IUserService, and the body
        // of the nightly non-compliance sweep. Orchestrators: no repository injected, and every
        // outbound edge is another section's leaf.
        services.AddScoped<IHumanLifecycleService, HumanLifecycleService>();
        services.AddScoped<INonCompliantMemberSuspension, NonCompliantMemberSuspension>();

        // Same shape: no repository, so orchestrators by the hard rules' definition.
        // ExternalLoginService keeps its Humans.Application.Services.Users namespace —
        // HUM0005 names it as the sole legal caller of ReconcileOAuthIdentityAsync by full name.
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        services.AddScoped<IExternalLoginService, ExternalLoginService>();
        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();

        // Audience-segmentation diagnostic for UsersAdminController.Audience.
        services.AddScoped<IUsersAudienceService, UsersAudienceService>();
    }
}
