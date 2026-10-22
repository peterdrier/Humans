using Humans.Shifts.Domain;
using Humans.Shifts.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Shifts.Data;

internal sealed class ShiftsDbContext(DbContextOptions<ShiftsDbContext> options)
    : DbContext(options)
{
    public DbSet<EventSettings> EventSettings => Set<EventSettings>();
    public DbSet<Rota> Rotas => Set<Rota>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ShiftSignup> ShiftSignups => Set<ShiftSignup>();
    public DbSet<ShiftTag> ShiftTags => Set<ShiftTag>();
    public DbSet<VolunteerEventProfile> VolunteerEventProfiles => Set<VolunteerEventProfile>();
    public DbSet<GeneralAvailability> GeneralAvailability => Set<GeneralAvailability>();
    public DbSet<VolunteerBuildStatus> VolunteerBuildStatuses => Set<VolunteerBuildStatus>();
    public DbSet<VolunteerTagPreference> VolunteerTagPreferences => Set<VolunteerTagPreference>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new EventSettingsConfiguration());
        builder.ApplyConfiguration(new RotaConfiguration());
        builder.ApplyConfiguration(new ShiftConfiguration());
        builder.ApplyConfiguration(new ShiftSignupConfiguration());
        builder.ApplyConfiguration(new ShiftTagConfiguration());
        builder.ApplyConfiguration(new VolunteerEventProfileConfiguration());
        builder.ApplyConfiguration(new GeneralAvailabilityConfiguration());
        builder.ApplyConfiguration(new VolunteerBuildStatusConfiguration());
        builder.ApplyConfiguration(new VolunteerTagPreferenceConfiguration());
    }
}
