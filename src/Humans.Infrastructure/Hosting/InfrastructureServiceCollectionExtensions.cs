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
            ConfigureNpgsql(sp, options, typeof(HumansDbContext));
            options.AddInterceptors(sp.GetRequiredService<QueryMonitoringInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
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
            ConfigureNpgsql(sp, options, typeof(HumansDbContext));
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
        });

        // Per-section contexts (nobodies-collective/Humans#858), migrated after
        // HumansDbContext by DatabaseMigrationHostedService in registration order.
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");
        services.AddSectionDbContext<EmailDbContext>(sentinelTable: "email_outbox_messages");
        services.AddSectionDbContext<CalendarDbContext>(sentinelTable: "calendar_events");
        services.AddSectionDbContext<NotificationsDbContext>(sentinelTable: "notifications");
        services.AddSectionDbContext<IssuesDbContext>(sentinelTable: "issues");
        services.AddSectionDbContext<GovernanceDbContext>(sentinelTable: "applications");
        services.AddSectionDbContext<CampaignsDbContext>(sentinelTable: "campaigns");
        services.AddSectionDbContext<GoogleIntegrationDbContext>(sentinelTable: "google_resources");
        services.AddSectionDbContext<TicketsDbContext>(sentinelTable: "ticket_orders");
        services.AddSectionDbContext<FeedbackDbContext>(sentinelTable: "feedback_reports");
        services.AddSectionDbContext<CityPlanningDbContext>(sentinelTable: "city_planning_settings");
        services.AddSectionDbContext<BudgetDbContext>(sentinelTable: "budget_years");
        services.AddSectionDbContext<CampsDbContext>(sentinelTable: "camps");
        services.AddSectionDbContext<GateDbContext>(sentinelTable: "gate_settings");
        services.AddSectionDbContext<SystemDbContext>(sentinelTable: "DataProtectionKeys");
        services.AddSectionDbContext<LegalDbContext>(sentinelTable: "legal_documents");
        services.AddSectionDbContext<AuditLogDbContext>(sentinelTable: "audit_log");

        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    /// <summary>
    /// Registers a per-section DbContext (nobodies-collective/Humans#858): scoped context +
    /// singleton factory with the same Npgsql options and interceptors as
    /// <see cref="HumansDbContext"/>, the section-specific history table from
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
        string? migrationsHistoryTable = null)
    {
        options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
        {
            npgsqlOptions.UseNodaTime();
            npgsqlOptions.MigrationsAssembly(contextType.Assembly.GetName().Name!);
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

    /// <summary>Typed wrapper so Web never references SystemDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToSystemDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<SystemDbContext>();
}
