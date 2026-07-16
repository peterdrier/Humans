using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Humans.Infrastructure.Hosting;

/// <summary>DI wiring for HumansDbContext, IDbContextFactory, migration runner, Identity stores.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers HumansDbContext + factory + migration runner. Caller must register
    /// NpgsqlDataSource and any interceptors first.
    /// </summary>
    public static IServiceCollection AddHumansPersistence(
        this IServiceCollection services,
        bool enableDeveloperDiagnostics)
    {
        // optionsLifetime: Singleton so the Singleton IDbContextFactory can consume DbContextOptions.
        services.AddDbContext<HumansDbContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options);
            options.AddInterceptors(sp.GetRequiredService<QueryMonitoringInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<LegalDocumentSaveChangesInterceptor>());
            // PK lookups via FirstOrDefaultAsync(e => e.Id == id) are deterministic — suppress warning.
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
            if (enableDeveloperDiagnostics)
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        }, optionsLifetime: ServiceLifetime.Singleton);

        // Singleton-lifetime factory so Singleton repositories can inject it without scope-validation issues.
        services.AddDbContextFactory<HumansDbContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options);
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<LegalDocumentSaveChangesInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
        });

        // Per-section contexts (nobodies-collective/Humans#858), migrated after
        // HumansDbContext by DatabaseMigrationHostedService in registration order.
        services.AddSectionDbContext<SystemSettingsDbContext>(sentinelTable: "system_settings");

        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    /// <summary>
    /// Registers a per-section DbContext (nobodies-collective/Humans#858): scoped context +
    /// singleton factory with the same Npgsql options and interceptors as
    /// <see cref="HumansDbContext"/>, a section-specific
    /// <c>__EFMigrationsHistory_&lt;Section&gt;</c> table, and the
    /// <see cref="SectionDbContextRegistration"/> consumed by
    /// <see cref="DatabaseMigrationHostedService"/> to run
    /// <see cref="SectionMigrationRunner"/> at startup.
    /// </summary>
    /// <param name="sentinelTable">See <see cref="SectionDbContextRegistration.SentinelTable"/>.</param>
    internal static IServiceCollection AddSectionDbContext<TContext>(
        this IServiceCollection services,
        string sentinelTable)
        where TContext : DbContext
    {
        // AgentDbContext -> __EFMigrationsHistory_Agent
        var historyTable = "__EFMigrationsHistory_" +
            typeof(TContext).Name.Replace("DbContext", "", StringComparison.Ordinal);

        services.AddDbContext<TContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options, historyTable);
            options.AddInterceptors(sp.GetRequiredService<QueryMonitoringInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<LegalDocumentSaveChangesInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
        }, optionsLifetime: ServiceLifetime.Singleton);

        services.AddDbContextFactory<TContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options, historyTable);
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<LegalDocumentSaveChangesInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
        });

        services.AddSingleton(new SectionDbContextRegistration(typeof(TContext), sentinelTable));

        return services;
    }

    private static void ConfigureNpgsql(
        IServiceProvider sp,
        DbContextOptionsBuilder options,
        string? migrationsHistoryTable = null)
    {
        options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
        {
            npgsqlOptions.UseNodaTime();
            npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            if (migrationsHistoryTable is not null)
            {
                npgsqlOptions.MigrationsHistoryTable(migrationsHistoryTable);
            }
        });
    }

    /// <summary>Typed wrapper so Web never references HumansDbContext directly.</summary>
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<HumansDbContext>();

    /// <summary>Typed wrapper so Web never references HumansDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToHumansDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<HumansDbContext>();
}
