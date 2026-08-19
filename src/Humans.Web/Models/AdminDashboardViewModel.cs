using Humans.Web.Services.Dashboard;

namespace Humans.Web.Models;

public sealed record AdminDashboardViewModel(
    DashboardApplicationStats AppStats,
    IReadOnlyList<DashboardLanguageCount> LanguageDistribution,
    UserSetMembership SetMembership);

public sealed record DashboardApplicationStats(
    int Total,
    int Approved,
    int Rejected,
    int Colaborador,
    int Asociado)
{
    public int Pending => Total - Approved - Rejected;
    public bool HasAny => Total > 0;
}

public sealed record DashboardLanguageCount(string Language, int Count);
