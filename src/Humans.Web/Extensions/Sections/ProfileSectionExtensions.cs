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
using ProfilesProfileEditorService = Humans.Application.Services.Profiles.ProfileEditorService;
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
        // Profile section — see #504. Singleton repos so CachingUserService injects directly without scope-factory.
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
        services.AddScoped<IProfileEditorService, ProfilesProfileEditorService>();
        services.AddScoped<IAccountProvisioningService, UsersAccountProvisioningService>();
        services.AddScoped<IUserEmailProviderBackfillService, UsersUserEmailProviderBackfillService>();

        // FullProfile cache retired — denormalized reads go through IUserService.GetUserInfoAsync.
        services.AddScoped<ProfilesProfileService>();
        services.AddScoped<IProfilePictureService>(sp => sp.GetRequiredService<ProfilesProfileService>());

        return services;
    }
}
