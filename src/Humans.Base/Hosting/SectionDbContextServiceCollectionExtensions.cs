using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Humans.Base.Hosting;

/// <summary>
/// The per-section DbContext registration seam. Lives in Base (this assembly) because all 28
/// table-owning sections call <see cref="AddSectionDbContext{TContext}"/> from their own
/// <c>Section.cs</c>, and a section may not reference <c>Humans.Infrastructure</c>
/// (nobodies-collective/Humans#866, G5 lane 3a-2).
/// </summary>
/// <remarks>
/// A separate class from <c>InfrastructureServiceCollectionExtensions</c>, which stays in
/// <c>Humans.Infrastructure</c> deliberately: two architecture rules anchor on
/// <c>typeof(InfrastructureServiceCollectionExtensions).Assembly</c> to mean "the
/// Humans.Infrastructure assembly", and moving the type would have retargeted both onto Base,
/// where they would pass vacuously. The namespace is shared with that class rather than renamed,
/// so extension-method lookup at all 28 call sites resolves exactly as before with no
/// <c>using</c> edit anywhere.
/// </remarks>
public static class SectionDbContextServiceCollectionExtensions
{
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
}
