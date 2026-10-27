using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Camps.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Calendar.Contracts;
using Humans.Email.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Filters;
using Humans.Events.Services;
using Humans.Events.ViewComponents;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Events;

public sealed class Section : ISection, IUserPart, ICampPart, ISectionRoles
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(EventsRoles.EventsAdmin, "Events Admin",
            "Tastemakers of the program. They review event guide submissions, request edits, and shepherd the schedule from rough draft to ready-for-prime-time.",
            "fa-solid fa-book-open"),
    ];

    IEnumerable<UserPart> IUserPart.Parts() => [new(UserPartSlots.Profile, typeof(EventsCardViewComponent))];

    IEnumerable<CampPart> ICampPart.Parts() => [new(typeof(EventsCardViewComponent))];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<EventGuideDbContext>(sentinelTable: "events");

        services.AddScoped<EventsFeatureFilter>();
        services.AddSingleton<IEventRepository, EventRepository>();

        services.AddScoped<EventsEmails>();
        services.AddScoped<IEmailPreviewContributor, EventsEmailPreviews>();

        services.AddKeyedScoped<IEventManagement, EventService>(CachingEventService.InnerServiceKey);
        services.AddScoped<EventService>(sp =>
            (EventService)sp.GetRequiredKeyedService<IEventManagement>(CachingEventService.InnerServiceKey));
        services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<EventService>());

        services.AddSingleton<CachingEventService>();
        services.AddSingleton<IEventManagement>(sp => sp.GetRequiredService<CachingEventService>());

        services.AddSingleton<IEventService>(sp => sp.GetRequiredService<CachingEventService>());

        // The guide settings projection carries Settings' TimeZoneId, so a Settings-side save must refresh it.
        services.AddSingleton<IEventSettingsChangeListener>(sp =>
            sp.GetRequiredService<CachingEventService>());

        // GDPR fan-out binds to the decorator, not the inner: erasure clears the
        // person's Host name on events that stay in the guide, and those rows are cached.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingEventService>());

        // Account-merge fan-out binds to the decorator too: the fold re-points the submitter
        // of cached approved events.
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingEventService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEventService>().EventCacheStats);

        // Also the hosted service that warms all four projections. Failures are logged; lazy loading still works.
        services.AddHostedService(sp => sp.GetRequiredService<CachingEventService>());
    }
}
