using System.Diagnostics;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Health;
using Humans.Web.Middleware;
using Microsoft.Extensions.Localization;
using Npgsql;
using Humans.Infrastructure.Logging;
using Serilog;

// PhotoSauce.NativeCodecs.Libheif ships the native library as "heif.dll" on Windows,
// but LibHeifSharp P/Invokes "libheif". Bridge the name difference.
// On Linux the .so is named correctly — no resolver needed.
Humans.Web.Helpers.LibHeifResolver.Register();

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Humans.Web")
    .Enrich.With<PiiRedactionEnricher>()
    .WriteTo.Console();

if (Debugger.IsAttached)
{
    var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "human");
    Directory.CreateDirectory(logDir);
    logConfig.WriteTo.File(
        Path.Combine(logDir, "humans-.log"),
        rollingInterval: RollingInterval.Day);
}

Log.Logger = logConfig.CreateLogger();

builder.Host.UseSerilog();

// Add services to the container

// Configure NodaTime clock
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// Configure JSON options with NodaTime support
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
});

// Configure Npgsql data source with NodaTime and dynamic JSON (for jsonb Dictionary columns).
// Registered as a DI singleton so the connection string is resolved at service-resolution time,
// allowing integration tests to override configuration via WebApplicationFactory.
builder.Services.AddSingleton(sp =>
{
    var connStr = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    var dsb = new NpgsqlDataSourceBuilder(connStr);
    dsb.UseNodaTime();
    dsb.EnableDynamicJson();
    return dsb.Build();
});

// Configure EF Core with PostgreSQL
builder.Services.AddDbContext<HumansDbContext>((sp, options) =>
{
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
    {
        npgsqlOptions.UseNodaTime();
        npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
    });
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// Persist Data Protection keys to the database so auth cookies survive container restarts
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<HumansDbContext>()
    .SetApplicationName("Humans.Web");

// Configure ASP.NET Core Identity
builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<HumansDbContext>()
    .AddDefaultTokenProviders();

// Configure cookie security policy (TLS terminated by Coolify/reverse proxy)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

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
        options.SaveTokens = false;
    });

// Configure Authorization
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleAssignmentClaimsTransformation>();

// Configure Hangfire
builder.Services.AddHangfire((sp, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(
            sp.GetRequiredService<IConfiguration>()
                .GetConnectionString("DefaultConnection")!)));

builder.Services.AddHangfireServer();

// Configure OpenTelemetry
var serviceName = "Humans.Web";
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
        .AddMeter("Humans.Metrics")
        .AddPrometheusExporter());

// Register activity source for custom tracing
builder.Services.AddSingleton(new ActivitySource(serviceName, serviceVersion));

// Configure Health Checks
builder.Services.AddHealthChecks()
    .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgresql")
    .AddHangfire(options => options.MinimumAvailableServers = 1, name: "hangfire")
    .AddCheck<ConfigurationHealthCheck>("configuration")
    .AddCheck<SmtpHealthCheck>("smtp")
    .AddCheck<GitHubHealthCheck>("github")
    .AddCheck<GoogleWorkspaceHealthCheck>("google-workspace");

builder.Services.AddHumansInfrastructure(builder.Configuration, builder.Environment);

// Configure Response Compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// Configure Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Forwarded headers (X-Forwarded-For, X-Forwarded-Proto) are enabled via
// ASPNETCORE_FORWARDEDHEADERS_ENABLED=true in the deployment environment.
// No explicit config needed — the app is only reachable through Traefik/Coolify
// on internal Docker networks, so trusting any proxy is safe.

// Configure Localization
builder.Services.AddLocalization();

// Add Controllers with Views
builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add<MembershipRequiredFilter>();
    })
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization();
builder.Services.AddRazorPages();

var supportedCultures = new[] { "en", "es", "de", "it", "fr" };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("en");
    options.AddSupportedCultures(supportedCultures);
    options.AddSupportedUICultures(supportedCultures);
    options.AddInitialRequestCultureProvider(new CustomRequestCultureProvider(async context =>
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userManager = context.RequestServices.GetRequiredService<UserManager<User>>();
            var user = await userManager.GetUserAsync(context.User);
            if (user != null && !string.IsNullOrEmpty(user.PreferredLanguage))
            {
                return new ProviderCultureResult(user.PreferredLanguage);
            }
        }
        return null;
    }));
});

var app = builder.Build();

// Eagerly resolve HumansMetricsService so the background gauge-refresh timer starts
// immediately — otherwise observable gauges emit nothing until first injection.
app.Services.GetRequiredService<HumansMetricsService>();

// Localization diagnostic check
{
    using var scope = app.Services.CreateScope();
    var localizerFactory = scope.ServiceProvider.GetRequiredService<IStringLocalizerFactory>();
    var localizer = localizerFactory.Create(typeof(Humans.Web.SharedResource));
    var testKey = "Dashboard_Welcome";
    var result = localizer[testKey];

    if (result.ResourceNotFound)
    {
        Log.Error("LOCALIZATION BROKEN: Resource key '{Key}' not found. SearchedLocation: {Location}",
            testKey, result.SearchedLocation);
        Log.Error("SharedResource type: {TypeName}, Assembly: {Assembly}",
            typeof(Humans.Web.SharedResource).FullName, typeof(Humans.Web.SharedResource).Assembly.GetName().Name);

        // List embedded resources for debugging
        var assembly = typeof(Humans.Web.SharedResource).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        Log.Error("Embedded resources in {Assembly}: {Resources}",
            assembly.GetName().Name, string.Join(", ", resourceNames));

        // Check satellite assemblies
        foreach (var culture in new[] { "en", "es", "de", "it", "fr" })
        {
            try
            {
                var satAssembly = assembly.GetSatelliteAssembly(new System.Globalization.CultureInfo(culture));
                var satResources = satAssembly.GetManifestResourceNames();
                Log.Information("Satellite assembly [{Culture}] resources: {Resources}",
                    culture, string.Join(", ", satResources));
            }
            catch (Exception ex)
            {
                Log.Warning("No satellite assembly for culture '{Culture}': {Error}", culture, ex.Message);
            }
        }
    }
    else
    {
        Log.Information("Localization OK: '{Key}' => '{Value}'", testKey, result.Value);
    }
}

// Configure the HTTP request pipeline

// Forwarded headers must be first (for reverse proxy)
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Home/Error/{0}");

app.UseHttpsRedirection();

// Response compression
app.UseResponseCompression();

app.UseStaticFiles();

// Serve .well-known directory (blocked by default since it starts with a dot)
if (app.Environment.IsDevelopment())
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(
            Path.Combine(app.Environment.WebRootPath, ".well-known")),
        RequestPath = "/.well-known",
        ServeUnknownFileTypes = true
    });
}

// HTTP Security Headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
    await next();
});

app.UseMiddleware<CspNonceMiddleware>();

app.UseRouting();

// Rate limiting
app.UseRateLimiter();

// Serilog request logging
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

app.UseRequestLocalization();

// Health check endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteDetailedHealthResponse
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // Liveness check - just confirms the app is running
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
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
        : [new Humans.Web.HangfireAuthorizationFilter()]
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

// Run database migrations on startup (must happen before Hangfire job registration
// because Hangfire needs its tables to exist for distributed lock acquisition)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Force Hangfire global configuration to initialize (sets JobStorage.Current)
// before registering recurring jobs. The AddHangfire((sp, config) => ...) overload
// defers the config lambda until IGlobalConfiguration is resolved from DI;
// RecurringJob.AddOrUpdate() uses the static JobStorage.Current, so we must
// ensure it's set first.
app.Services.GetRequiredService<IGlobalConfiguration>();
app.UseHumansRecurringJobs();

await app.RunAsync();

static async Task WriteDetailedHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    var result = new
    {
        status = report.Status.ToString(),
        results = report.Entries.ToDictionary(
            e => e.Key,
            e => new
            {
                status = e.Value.Status.ToString(),
                duration = e.Value.Duration.ToString()
            },
            StringComparer.Ordinal)
    };

    await context.Response.WriteAsJsonAsync(result);
}

// Make Program accessible to WebApplicationFactory<Program> in integration tests
public partial class Program;
