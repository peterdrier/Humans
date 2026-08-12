using System.Globalization;
using Humans.Application.Interfaces;
using Humans.Holded.Data;
using Humans.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Holded;

/// <summary>
/// Holded's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// No GDPR contributor: the mirror has no user-scoped table. The member→creditor-account
/// binding stays in Finance, which owns that meaning and exports it.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Sentinel choice is load-bearing: "the sentinel exists" must PROVE this baseline already
        // ran. holded_ledger_lines cannot carry that proof — the historical root migration chain
        // and Finance's pre-split model both created it, so on existing databases it exists while
        // holded_accounts/holded_api_calls/holded_sync_states do not, and a sentinel hit would
        // record the baseline as applied with three tables missing. holded_accounts is created by
        // this baseline alone. Ordering still matters once per boot: sections migrate in name
        // order, so Finance's drop of its holded_ledger_lines/holded_sync_states runs before this
        // baseline recreates them under Holded ownership.
        services.AddSectionDbContext<HoldedDbContext>(sentinelTable: "holded_accounts");

        // The plan-tier call allowance, which no Holded endpoint reports (GET /usage's `limit`
        // is the billable-overage ceiling). Left at the default when unset or unparseable.
        services.Configure<HoldedSectionOptions>(opts =>
        {
            if (int.TryParse(configuration["Holded:MonthlyCallBudget"],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) && budget > 0)
                opts.MonthlyCallBudget = budget;
        });

        services.AddScoped<IHoldedMirrorRepository, Repository>();
        services.AddScoped<Services.Service>();
        services.AddScoped<Contracts.IHoldedService>(sp => sp.GetRequiredService<Services.Service>());
        services.AddScoped<Services.IHoldedAdminService>(sp => sp.GetRequiredService<Services.Service>());
    }
}
