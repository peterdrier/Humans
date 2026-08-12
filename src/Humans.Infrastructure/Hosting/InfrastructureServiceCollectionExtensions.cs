using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
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
        // Per-section contexts (nobodies-collective/Humans#858), migrated by
        // DatabaseMigrationHostedService in registration order. Users carries the
        // Identity tables; its sentinel is the chain-created users table.
        services.AddSectionDbContext<UsersDbContext>(sentinelTable: "users");
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");
        services.AddSectionDbContext<GoogleIntegrationDbContext>(sentinelTable: "google_resources");
        services.AddSectionDbContext<TicketsDbContext>(sentinelTable: "ticket_orders");
        services.AddSectionDbContext<CampsDbContext>(sentinelTable: "camps");
        services.AddSectionDbContext<SystemDbContext>(sentinelTable: "DataProtectionKeys");
        services.AddSectionDbContext<AuditLogDbContext>(sentinelTable: "audit_log");
        services.AddSectionDbContext<ShiftsDbContext>(sentinelTable: "shifts");
        services.AddSectionDbContext<TeamsDbContext>(sentinelTable: "teams");

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
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
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
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
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

    /// <summary>Typed wrapper so Web never references UsersDbContext directly.</summary>
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<UsersDbContext>();

    /// <summary>Typed wrapper so Web never references SystemDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToSystemDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<SystemDbContext>();
}
