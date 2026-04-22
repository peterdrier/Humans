using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.HostedServices;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Profiles;
using ProfilesProfileService = Humans.Application.Services.Profile.ProfileService;
using ProfilesContactFieldService = Humans.Application.Services.Profile.ContactFieldService;
using ProfilesUserEmailService = Humans.Application.Services.Profile.UserEmailService;
using ProfilesCommunicationPreferenceService = Humans.Application.Services.Profile.CommunicationPreferenceService;
using ProfilesContactService = Humans.Application.Services.Profile.ContactService;

namespace Humans.Web.Extensions.Sections;

internal static class ProfileSectionExtensions
{
    internal static IServiceCollection AddProfileSection(this IServiceCollection services)
    {
        // Profile section — repository/store/decorator pattern (§15 Step 0, PR #504)
        // Repositories use IDbContextFactory and are registered as Singleton so the
        // CachingProfileService Singleton can inject them directly without scope-factory indirection.
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IContactFieldRepository, ContactFieldRepository>();
        services.AddSingleton<IUserEmailRepository, UserEmailRepository>();
        services.AddSingleton<ICommunicationPreferenceRepository, CommunicationPreferenceRepository>();

        services.AddScoped<IUnsubscribeTokenProvider, UnsubscribeTokenProvider>();

        services.AddScoped<ICommunicationPreferenceService, ProfilesCommunicationPreferenceService>();
        services.AddScoped<IUnsubscribeService, UnsubscribeService>();

        services.AddScoped<IContactFieldService, ProfilesContactFieldService>();
        services.AddScoped<IUserEmailService, ProfilesUserEmailService>();
        services.AddScoped<IEmailProvisioningService, EmailProvisioningService>();

        services.AddScoped<AccountMergeService>();
        services.AddScoped<IAccountMergeService>(sp => sp.GetRequiredService<AccountMergeService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AccountMergeService>());

        services.AddScoped<IDuplicateAccountService, DuplicateAccountService>();
        services.AddScoped<IContactService, ProfilesContactService>();
        services.AddScoped<IAccountProvisioningService, AccountProvisioningService>();

        // ProfileService (inner): Scoped — has many Scoped cross-section deps.
        // Registered under the keyed "profile-inner" key so CachingProfileService can
        // resolve it from a scope without triggering self-resolution on the unkeyed
        // IProfileService registration (which maps to the Singleton decorator).
        services.AddKeyedScoped<IProfileService, ProfilesProfileService>(CachingProfileService.InnerServiceKey);
        services.AddScoped<ProfilesProfileService>(sp =>
            (ProfilesProfileService)sp.GetRequiredKeyedService<IProfileService>(CachingProfileService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ProfilesProfileService>());

        // CachingProfileService: Singleton so the _byUserId ConcurrentDictionary persists
        // across requests. Resolves the Scoped inner IProfileService (keyed "profile-inner")
        // and other Scoped deps (IUserService, INavBadgeCacheInvalidator,
        // INotificationMeterCacheInvalidator) per-call via IServiceScopeFactory to avoid
        // the captured-scoped-dep anti-pattern.
        // IProfileRepository and IUserEmailRepository are injected directly because they
        // are also Singleton (IDbContextFactory-based).
        services.AddSingleton<CachingProfileService>();
        services.AddSingleton<IProfileService>(sp => sp.GetRequiredService<CachingProfileService>());

        // CRITICAL: IFullProfileInvalidator must resolve to the same Singleton decorator instance
        // that backs IProfileService. Both interfaces share the single CachingProfileService
        // instance, so the _byUserId dict is never split.
        services.AddSingleton<IFullProfileInvalidator>(sp =>
            sp.GetRequiredService<CachingProfileService>());

        // Eagerly warm the FullProfile dict at startup so bulk reads
        // (birthday widget, location directory, admin human list, profile search)
        // return complete results immediately after deploy instead of filling
        // in lazily per user. Failures are logged and swallowed; lazy population
        // still works.
        services.AddHostedService<FullProfileWarmupHostedService>();

        return services;
    }
}
