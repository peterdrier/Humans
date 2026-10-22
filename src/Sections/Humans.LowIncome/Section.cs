using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.LowIncome;

/// <summary>Owns no tables or services: its one page writes through Grants' <c>IGrantWrite</c>.</summary>
public sealed class Section : ISection, ISectionAdminNav
{
    /// <summary>The open Grants program with this source key is the one LowIncome fills.</summary>
    internal const string SourceKey = "low-income";

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Low income", [
            new("Grants", "LowIncomeAdmin", "Index", null, null,
                "fa-solid fa-hand-holding-heart", PolicyNames.AdminOnly)
        ])
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
