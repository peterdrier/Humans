using Humans.Events.Domain;
using Humans.Events.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Events.Data;

/// <summary>
/// Per-section database context for the EventGuide section
/// (nobodies-collective/Humans#858): maps only <c>events</c>,
/// <c>event_categories</c>, <c>event_venues</c>, <c>event_guide_settings</c>,
/// <c>event_moderation_actions</c>, <c>event_favourites</c> and
/// <c>event_preferences</c>, with its own
/// <c>__EFMigrationsHistory_EventGuide</c> table and migrations under
/// <c>Migrations/EventGuide/</c>. Same database, same connection — the split is
/// a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// The Shifts-owned <c>event_settings</c> and <c>event_participations</c> tables
/// stay in the Shifts context and are deliberately absent here.
/// </remarks>
internal sealed class EventGuideDbContext(DbContextOptions<EventGuideDbContext> options)
    : DbContext(options)
{
    public DbSet<EventGuideSettings> EventGuideSettings => Set<EventGuideSettings>();
    public DbSet<EventCategory> EventCategories => Set<EventCategory>();
    public DbSet<EventVenue> EventVenues => Set<EventVenue>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventModerationAction> EventModerationActions => Set<EventModerationAction>();
    public DbSet<EventPreference> EventPreferences => Set<EventPreference>();
    public DbSet<EventFavourite> EventFavourites => Set<EventFavourite>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new EventGuideSettingsConfiguration());
        builder.ApplyConfiguration(new EventCategoryConfiguration());
        builder.ApplyConfiguration(new EventVenueConfiguration());
        builder.ApplyConfiguration(new EventConfiguration());
        builder.ApplyConfiguration(new EventModerationActionConfiguration());
        builder.ApplyConfiguration(new EventPreferenceConfiguration());
        builder.ApplyConfiguration(new EventFavouriteConfiguration());
    }
}
