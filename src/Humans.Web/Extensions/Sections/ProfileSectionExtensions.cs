using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Services.Profiles;
using ProfilesProfileService = Humans.Application.Services.Profiles.ProfileService;
using ProfilesContactFieldService = Humans.Application.Services.Profiles.ContactFieldService;
using ProfilesUserEmailService = Humans.Application.Services.Profiles.UserEmailService;
using ProfilesCommunicationPreferenceService = Humans.Application.Services.Profiles.CommunicationPreferenceService;
using ProfilesAccountMergeService = Humans.Application.Services.Profiles.AccountMergeService;
using ProfilesDuplicateAccountService = Humans.Application.Services.Profiles.DuplicateAccountService;
using ProfilesEmailProblemsService = Humans.Application.Services.Profiles.EmailProblemsService;
using UsersAccountProvisioningService = Humans.Application.Services.Users.AccountProvisioningService;
using UsersUserEmailProviderBackfillService = Humans.Application.Services.Users.UserEmailProviderBackfillService;
using UsersUnsubscribeService = Humans.Application.Services.Users.UnsubscribeService;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Profiles;

namespace Humans.Web.Extensions.Sections;

internal static class ProfileSectionExtensions
{
    internal static IServiceCollection AddProfileSection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Profile section — repository/store pattern (§15 Step 0, PR #504).
        // Repositories use IDbContextFactory and are registered as Singleton so the
        // Singleton CachingUserService can inject them directly without scope-factory indirection.
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IContactFieldRepository, ContactFieldRepository>();
        services.AddSingleton<IUserEmailRepository, UserEmailRepository>();
        services.AddSingleton<ICommunicationPreferenceRepository, CommunicationPreferenceRepository>();
        services.AddSingleton<IAccountMergeRepository, AccountMergeRepository>();

        services.AddScoped<IUnsubscribeTokenProvider, UnsubscribeTokenProvider>();

        services.AddScoped<ProfilesCommunicationPreferenceService>();
        services.AddScoped<ICommunicationPreferenceService>(sp => sp.GetRequiredService<ProfilesCommunicationPreferenceService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ProfilesCommunicationPreferenceService>());

        services.AddScoped<IUnsubscribeService, UsersUnsubscribeService>();

        services.AddScoped<ProfilesContactFieldService>();
        services.AddScoped<IContactFieldService>(sp => sp.GetRequiredService<ProfilesContactFieldService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ProfilesContactFieldService>());

        services.AddScoped<ProfilesUserEmailService>();
        services.AddScoped<IUserEmailService>(sp => sp.GetRequiredService<ProfilesUserEmailService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ProfilesUserEmailService>());

        services.AddScoped<ProfilesAccountMergeService>();
        services.AddScoped<IAccountMergeService>(sp => sp.GetRequiredService<ProfilesAccountMergeService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ProfilesAccountMergeService>());

        services.AddScoped<IDuplicateAccountService, ProfilesDuplicateAccountService>();
        services.AddScoped<IEmailProblemsService, ProfilesEmailProblemsService>();
        services.AddScoped<IAccountProvisioningService, UsersAccountProvisioningService>();
        services.AddScoped<IUserEmailProviderBackfillService, UsersUserEmailProviderBackfillService>();

        // ProfileService: Scoped — owns Profile-table writes. Registered directly
        // (no caching decorator) since the FullProfile cache was retired in favour
        // of the unified UserInfo cache on CachingUserService. Profile reads that
        // need a denormalized "everything-about-a-person" projection go through
        // IUserService.GetUserInfoAsync instead.
        services.AddScoped<ProfilesProfileService>();
        services.AddScoped<IProfileService>(sp => sp.GetRequiredService<ProfilesProfileService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ProfilesProfileService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ProfilesProfileService>());

        return services;
    }
}
