using System.Diagnostics;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Infrastructure.Data;
using Profiles.Infrastructure.Jobs;
using Profiles.Infrastructure.Repositories;
using Profiles.Infrastructure.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Profiles.Web")
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// Configure NodaTime clock
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// Configure JSON options with NodaTime support
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
});

// Configure EF Core with PostgreSQL and NodaTime
builder.Services.AddDbContext<ProfilesDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.UseNodaTime();
        npgsqlOptions.MigrationsAssembly("Profiles.Infrastructure");
    }));

// Configure ASP.NET Core Identity
builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<ProfilesDbContext>()
    .AddDefaultTokenProviders();

// Configure Authentication with Google OAuth
builder.Services.AddAuthentication()
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"]
            ?? throw new InvalidOperationException("Google ClientId not configured.");
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]
            ?? throw new InvalidOperationException("Google ClientSecret not configured.");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.SaveTokens = true;
    });

// Configure Authorization
builder.Services.AddAuthorization();

// Configure Hangfire
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer();

// Configure OpenTelemetry
var serviceName = "Profiles.Web";
var serviceVersion = "1.0.0";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .WithTracing(tracing => tracing
        .AddSource(serviceName)
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(
                builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317");
        }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("Profiles.Metrics")
        .AddPrometheusExporter());

// Register activity source for custom tracing
builder.Services.AddSingleton(new ActivitySource(serviceName, serviceVersion));

// Configure Health Checks
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql")
    .AddHangfire(options => options.MinimumAvailableServers = 1, name: "hangfire")
    .AddUrlGroup(new Uri("https://api.github.com"), name: "github-api");

// Register Application Services
builder.Services.AddScoped<IConsentRecordRepository, ConsentRecordRepository>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<IContactFieldService, ContactFieldService>();
builder.Services.AddScoped<IGoogleSyncService, StubGoogleSyncService>();
builder.Services.AddScoped<IMembershipCalculator, MembershipCalculator>();
builder.Services.AddScoped<SystemTeamSyncJob>();

// Add Controllers with Views
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Serilog request logging
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

// Health check endpoints
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false // Liveness check - just confirms the app is running
});

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => true // Readiness check - confirms all dependencies are available
});

// Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint("/metrics");

// Hangfire dashboard (admin only in production)
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = app.Environment.IsDevelopment()
        ? []
        : [new Profiles.Web.HangfireAuthorizationFilter()]
});

// Schedule recurring jobs
RecurringJob.AddOrUpdate<SystemTeamSyncJob>(
    "system-team-sync",
    job => job.ExecuteAsync(CancellationToken.None),
    Cron.Hourly);

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

// Run database migrations in development
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
    await dbContext.Database.MigrateAsync();
}

await app.RunAsync();
