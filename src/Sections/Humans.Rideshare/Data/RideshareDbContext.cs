using Humans.Rideshare.Data.Configurations;
using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Rideshare.Data;

/// <summary>
/// Per-section database context for the Rideshare section: maps only
/// <c>rideshare_trips</c>, <c>rideshare_requests</c>, <c>rideshare_interests</c> and
/// <c>rideshare_settings</c>, with its own <c>__EFMigrationsHistory_Rideshare</c> table
/// and migrations under <c>Data/Migrations/</c>. Same database, same connection — the
/// split is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: the repository is the only consumer.
/// Configurations are applied explicitly (not by assembly scanning) so this model can
/// never accrete another section's tables. Cross-section references (<c>UserId</c>,
/// <c>FromUserId</c>) are bare Guid columns, never FKs.
/// </remarks>
internal sealed class RideshareDbContext(DbContextOptions<RideshareDbContext> options)
    : DbContext(options)
{
    public DbSet<RideshareTrip> Trips => Set<RideshareTrip>();
    public DbSet<RideshareRequest> Requests => Set<RideshareRequest>();
    public DbSet<RideshareInterest> Interests => Set<RideshareInterest>();
    public DbSet<RideshareSettings> Settings => Set<RideshareSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new RideshareTripConfiguration());
        builder.ApplyConfiguration(new RideshareRequestConfiguration());
        builder.ApplyConfiguration(new RideshareInterestConfiguration());
        builder.ApplyConfiguration(new RideshareSettingsConfiguration());
    }
}
