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

/// <summary>No caching decorator: a stale gate read admits or blocks the wrong person.</summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<GateDbContext>(sentinelTable: "gate_settings");

        services.AddSingleton<IGateRepository, GateRepository>();

        // Identity registers only IPasswordHasher<User>, so register the GateStaffPin one.
        services.AddSingleton<IPasswordHasher<GateStaffPin>, PasswordHasher<GateStaffPin>>();

        services.AddSingleton<GatePinThrottle>();
        services.AddSingleton<GateVendorMirrorLedger>();

        services.AddScoped<GateService>();
        services.AddScoped<IGateService>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IGateScanRetention>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GateService>());

        services.AddScoped<GateRetentionJob>();
        services.AddScoped<GateVendorCheckInJob>();
    }
}
