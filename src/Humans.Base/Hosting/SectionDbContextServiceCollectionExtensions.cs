using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Humans.Base.Hosting;

/// <summary>
/// The per-section DbContext registration seam, in Base because every table-owning section calls
/// <see cref="AddSectionDbContext{TContext}"/> from its own <c>Section.cs</c>.
/// </summary>
public static class SectionDbContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers a per-section DbContext: scoped context plus singleton factory with shared Npgsql options and
    /// interceptors, the history table from <see cref="SectionMigrationsHistory"/> (shared with the design-time
    /// factories, so they never disagree), and the <see cref="SectionDbContextRegistration"/> the startup migration
    /// runner consumes.
    /// </summary>
    /// <param name="services">The section’s service collection.</param>
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
