using Humans.Domain.Entities;
using Humans.Shifts.Domain;
using Humans.Shifts.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Humans.Shifts.Data;

namespace Humans.Shifts.Data;

/// <summary>
/// Per-section database context for the Shifts section
/// (nobodies-collective/Humans#858): maps only <c>event_settings</c>,
/// <c>rotas</c>, <c>shifts</c>, <c>shift_signups</c>, <c>shift_tags</c>,
/// <c>rota_shift_tags</c>, <c>volunteer_event_profiles</c>,
/// <c>general_availability</c>, <c>volunteer_build_statuses</c> and
/// <c>volunteer_tag_preferences</c>, with its own
/// <c>__EFMigrationsHistory_Shifts</c> table and migrations under
/// <c>Migrations/Shifts/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Every user reference on these tables (<c>UserId</c>, <c>EnrolledByUserId</c>,
/// <c>ReviewedByUserId</c>, <c>SetByUserId</c>) is a bare Guid, so the Identity
/// tables stay outside this model and are deliberately absent here.
/// <c>event_participations</c> is a Users-section table
/// (<c>Configurations/Users</c>, <c>UserRepository</c>) and does not move with
/// this section.
/// </remarks>
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
