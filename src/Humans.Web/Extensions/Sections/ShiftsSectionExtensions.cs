using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using ShiftsShiftManagementService = Humans.Application.Services.Shifts.ShiftManagementService;
using ShiftsShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;
using ShiftsGeneralAvailabilityService = Humans.Application.Services.Shifts.GeneralAvailabilityService;
using ShiftsVolunteerTrackingService = Humans.Application.Services.Shifts.VolunteerTrackingService;
using ShiftsShiftViewService = Humans.Application.Services.Shifts.ShiftViewService;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Infrastructure.Services.Shifts;

namespace Humans.Web.Extensions.Sections;

internal static class ShiftsSectionExtensions
{
    internal static IServiceCollection AddShiftsSection(this IServiceCollection services)
    {
        // Shift management services — §15 repository pattern (issue #541a).
        // Repository is Singleton (IDbContextFactory-based). Service is Scoped
        // so it can pull per-request ITeamService/IUserService/ITicketQueryService.
        // IShiftAuthorizationInvalidator is aliased to the same Scoped instance
        // so Profile/User section writes (and anywhere else that needs to drop the
        // 60s shift-auth cache) resolve the same object as IShiftManagementService.
        services.AddSingleton<IShiftManagementRepository, ShiftManagementRepository>();
        services.AddScoped<ShiftsShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IShiftAuthorizationInvalidator>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());

        // ShiftSignupService — §15 repository pattern (issue #541, sub-task b).
        // Lives in Humans.Application.Services.Shifts; goes through
        // IShiftSignupRepository. Repository is Scoped because mutation flows
        // load-mutate-audit-save across multiple steps in one transaction.
        services.AddScoped<IShiftSignupRepository, ShiftSignupRepository>();
        services.AddScoped<ShiftsShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsShiftSignupService>());

        // General Availability — §15 repository pattern (issue #541, sub-task c).
        // Application-layer service goes through IGeneralAvailabilityRepository;
        // no caching decorator (Option A — small admin/self-service surface,
        // same rationale as Users/#243 and Audit Log/#552).
        services.AddSingleton<IGeneralAvailabilityRepository, GeneralAvailabilityRepository>();
        services.AddScoped<ShiftsGeneralAvailabilityService>();
        services.AddScoped<IGeneralAvailabilityService>(sp => sp.GetRequiredService<ShiftsGeneralAvailabilityService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ShiftsGeneralAvailabilityService>());

        // Volunteer Tracking — §15 repository pattern (volunteer-tracking feature).
        // Repository is Scoped (uses HumansDbContext directly, same pattern as
        // ShiftSignupRepository) so multi-step camp-setup + blocked-day mutations
        // share one EF change-tracker.
        services.AddScoped<IVolunteerTrackingRepository, VolunteerTrackingRepository>();
        services.AddScoped<IVolunteerTrackingService, ShiftsVolunteerTrackingService>();

        // ShiftView — issue #720. Singleton caching decorator over a Scoped
        // inner. Mirrors the Profiles / Teams pattern (CachingProfileService,
        // CachingTeamService). The inner is registered keyed so the Singleton
        // decorator can resolve a fresh Scoped instance per cache miss via
        // IServiceScopeFactory without self-resolving the unkeyed
        // IShiftView registration.
        services.AddKeyedScoped<IShiftView, ShiftsShiftViewService>(CachingShiftViewService.InnerServiceKey);
        services.AddSingleton<CachingShiftViewService>();
        services.AddSingleton<IShiftView>(sp => sp.GetRequiredService<CachingShiftViewService>());
        services.AddSingleton<IShiftViewInvalidator>(sp => sp.GetRequiredService<CachingShiftViewService>());

        // Surface both ShiftView caches (User + Rota) on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().UserCacheStats);
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingShiftViewService>().RotaCacheStats);

        return services;
    }
}
