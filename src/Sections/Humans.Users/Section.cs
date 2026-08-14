using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Humans.Users.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Data.Repositories;
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
/// Two registrations did <b>not</b> come across. <c>IDashboardService</c> and
/// <c>IAdminDashboardService</c> sat inside <c>AddUsersSection</c> but belong to Dashboard,
/// a Base orchestrator pair that aggregates from every section's services; they moved to
/// Shell's <c>ServiceCollectionExtensions</c> (Governance's rule — the section that owns the
/// file is not always the section that owns the line).
/// </para>
/// <para>
/// The <c>CachingUserService</c> block is copied as-is and is deliberately not tidied: one
/// Singleton resolved six ways, an <c>AddHostedService</c> for its startup warm, and the inner
/// <c>UserService</c> registered keyed with a Scoped unwrap so the decorator can open a scope
/// per call.
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
        services.AddScoped<IProfileEditorService, ProfileEditorService>();
        services.AddScoped<IAccountProvisioningService, AccountProvisioningService>();
        services.AddScoped<IUserEmailProviderBackfillService, UserEmailProviderBackfillService>();

        // Resource-based authorization handler for UserEmail operations. The *policy* stays in
        // Shell's AuthorizationPolicyExtensions; the handler moves in with the section
        // (design §15 step 6's asymmetry).
        services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();

        // FullProfile cache retired — denormalized reads go through IUserService.GetUserInfoAsync.
        services.AddScoped<ProfileService>();
        services.AddScoped<IProfilePictureService>(sp => sp.GetRequiredService<ProfileService>());
    }
}
