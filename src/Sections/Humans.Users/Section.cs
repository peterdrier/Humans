using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Application.Services.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Humans.Base.Hosting;
using Humans.Users.Authorization;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Data.Repositories;
using Humans.Users.Jobs;
using Humans.Users.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

public sealed class Section : ISection, IIssueQueueOwner, ISectionRoles
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
        services.AddSingleton<INotificationSourceContributor, UsersNotificationSources>();
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
}
