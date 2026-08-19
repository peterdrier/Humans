using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Gate.Contracts;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Jobs;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Base.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Gate;

/// <summary>
/// Gate's DI entry point, at the project root by convention. Discovered by Shell — nothing
/// names it, so it needs no section prefix. Replaces Shell's <c>AddGateSection</c> verbatim
/// (design §6): same lifetimes, same order.
/// </summary>
/// <remarks>
/// <para>
/// <c>GateRetentionJob</c> and <c>GateVendorCheckInJob</c> live in this project's
/// <c>Jobs/</c> folder (moved there from <c>Contracts/</c> by the HUM0034 carve-out,
/// nobodies-collective/Humans#1353); their registration moved here at #1074's jobs seam.
/// Only <c>GateRetentionJob</c> is a recurring job, contributed via <c>SectionJobs.cs</c>;
/// <c>GateVendorCheckInJob</c> is enqueued fire-and-forget on admit and needs only DI
/// registration. The retention job reaches the section through
/// <see cref="IGateScanRetention"/>; the vendor mirror job needs no Gate type at all, only
/// Tickets' <c>ITicketVendorMirror</c>.
/// </para>
/// <para>
/// No caching decorator: gate reads must be live (a stale verdict admits or blocks the wrong
/// person), mirroring the read-through Scanner section.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<GateDbContext>(sentinelTable: "gate_settings");

        // Repository: Singleton with a short-lived DbContext via IDbContextFactory,
        // matching the other section repositories.
        services.AddSingleton<IGateRepository, GateRepository>();

        // Personal staff-PIN hashing reuses ASP.NET Identity's vetted PBKDF2 hasher
        // (Identity only registers IPasswordHasher<User>, so register the GateStaffPin one).
        services.AddSingleton<IPasswordHasher<GateStaffPin>, PasswordHasher<GateStaffPin>>();

        // Kiosk-local state: the override-PIN brute-force throttle and the vendor mirror
        // ledger. Singletons over the shared IMemoryCache, as they were in Shell.
        services.AddSingleton<GatePinThrottle>();
        services.AddSingleton<GateVendorMirrorLedger>();

        // Service: Scoped — it composes cross-section reads and the gate repository.
        // Registered as its concrete type so the GDPR-contributor and account-merge
        // forwarding factories resolve the same section service (§8a / merge fan-out).
        services.AddScoped<GateService>();
        services.AddScoped<IGateService>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IGateScanRetention>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GateService>());

        services.AddScoped<GateRetentionJob>();
        services.AddScoped<GateVendorCheckInJob>();
    }
}
