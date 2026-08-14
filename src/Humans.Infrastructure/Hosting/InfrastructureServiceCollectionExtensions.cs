using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Humans.Infrastructure.Hosting;

/// <summary>DI wiring for the section DbContexts, IDbContextFactory, migration runner, Identity stores.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-section contexts + factories + migration runner. Caller must
    /// register NpgsqlDataSource and any interceptors first.
    /// </summary>
    public static IServiceCollection AddHumansPersistence(this IServiceCollection services)
    {
        // Base's own context (nobodies-collective/Humans#858), migrated by
        // DatabaseMigrationHostedService alongside every section's. UsersDbContext used to be
        // registered here too — it carries the Identity tables and was the last context left
        // in Base — and it moved into Humans.Users' Section.Register with the section
        // (#866, G5 lane 2, design §15 step 11).
        services.AddSectionDbContext<SystemDbContext>(sentinelTable: "DataProtectionKeys");

        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    /// <summary>
    /// Registers a per-section DbContext (nobodies-collective/Humans#858): scoped context +
    /// singleton factory with shared Npgsql options and interceptors, the
    /// section-specific history table from
    /// <see cref="SectionMigrationsHistory"/> (the same helper the design-time
    /// factories use, so the two can never disagree), and the
    /// <see cref="SectionDbContextRegistration"/> consumed by
    /// <see cref="DatabaseMigrationHostedService"/> to run
    /// <see cref="SectionMigrationRunner"/> at startup.
    /// </summary>
    /// <param name="sentinelTable">See <see cref="SectionDbContextRegistration.SentinelTable"/>.</param>
    public static IServiceCollection AddSectionDbContext<TContext>(
        this IServiceCollection services,
        string sentinelTable)
        where TContext : DbContext
    {
        var historyTable = SectionMigrationsHistory.TableFor<TContext>();

        services.AddDbContext<TContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options, typeof(TContext), historyTable);
            options.AddInterceptors(sp.GetRequiredService<QueryMonitoringInterceptor>());
            options.AddInterceptors(sp.GetServices<IInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
            if (sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        }, optionsLifetime: ServiceLifetime.Singleton);

        services.AddDbContextFactory<TContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options, typeof(TContext), historyTable);
            options.AddInterceptors(sp.GetServices<IInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
        });

        services.AddSingleton(new SectionDbContextRegistration(typeof(TContext), sentinelTable));

        return services;
    }

    private static void ConfigureNpgsql(
        IServiceProvider sp,
        DbContextOptionsBuilder options,
        Type contextType,
        string migrationsHistoryTable)
    {
        options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
        {
            npgsqlOptions.UseNodaTime();
            npgsqlOptions.MigrationsAssembly(contextType.Assembly.GetName().Name!);
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            npgsqlOptions.MigrationsHistoryTable(migrationsHistoryTable);
        });
    }

    /// <summary>Typed wrapper so Web never references SystemDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToSystemDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<SystemDbContext>();
}
