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

        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    private static void ConfigureNpgsql(IServiceProvider sp, DbContextOptionsBuilder options)
    {
        options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
        {
            npgsqlOptions.UseNodaTime();
            npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        });
    }

    /// <summary>Typed wrapper so Web never references HumansDbContext directly.</summary>
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<HumansDbContext>();

    /// <summary>Typed wrapper so Web never references HumansDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToHumansDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<HumansDbContext>();
}
