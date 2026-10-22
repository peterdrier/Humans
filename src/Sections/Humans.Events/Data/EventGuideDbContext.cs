using Humans.Events.Domain;
using Humans.Events.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Events.Data;

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
