using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.LowIncome.Controllers;

namespace Humans.LowIncome;

/// <summary>Owns no tables or services: its one page writes through Grants' <c>IGrantWrite</c>.</summary>
public sealed class Section : SectionBase, ISectionAdminNav
{
    public override string DisplayName => "Low Income";

    /// <summary>The open Grants program with this source key is the one LowIncome fills.</summary>
    internal const string SourceKey = "low-income";

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<LowIncomeAdminController>("Grants", nameof(LowIncomeAdminController.Index), "fa-solid fa-hand-holding-heart", PolicyNames.AdminOnly)
        ])
    ];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
