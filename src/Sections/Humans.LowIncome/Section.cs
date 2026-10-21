using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.LowIncome;

/// <summary>
/// LowIncome's DI entry point. Register is empty: the section owns no tables and no services
/// yet. Its one page issues and revokes Low Income grants through Grants' <c>IGrantWrite</c>.
/// </summary>
public sealed class Section : ISection, ISectionAdminNav
{
    /// <summary>The source key LowIncome passes to Grants; the open program with this key is the one it fills.</summary>
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
