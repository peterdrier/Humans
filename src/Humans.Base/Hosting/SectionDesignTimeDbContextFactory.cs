using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Base.Hosting;

/// <summary>
/// The design-time factory <c>dotnet ef</c> uses for a section's context, with the same Npgsql options and history
/// table as <see cref="SectionDbContextServiceCollectionExtensions.AddSectionDbContext{TContext}"/>. Each section keeps
/// a sealed subclass in its own assembly, because EF finds the factory by scanning the context's assembly.
/// </summary>
public abstract class SectionDesignTimeDbContextFactory<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : DbContext
{
    private const string DefaultConnection =
        "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

    /// <inheritdoc />
    public TContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ?? DefaultConnection;

        var optionsBuilder = new DbContextOptionsBuilder<TContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
            SectionDbContextServiceCollectionExtensions.ConfigureNpgsql(
                npgsqlOptions, typeof(TContext), SectionMigrationsHistory.TableFor<TContext>()));

        return Create(optionsBuilder.Options);
    }

    /// <summary>Constructs the context from the configured options.</summary>
    protected abstract TContext Create(DbContextOptions<TContext> options);
}
