using Humans.Rideshare.Data.Configurations;
using Humans.Rideshare.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Rideshare.Data;

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
