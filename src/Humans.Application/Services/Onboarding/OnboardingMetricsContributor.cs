using System.Diagnostics.Metrics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Onboarding;

namespace Humans.Application.Services.Onboarding;

/// <summary>
/// Onboarding section's push-model metrics contributor (issue nobodies-collective/Humans#580).
/// Counter-only; no gauges. Singleton so counter references persist across scopes.
/// </summary>
public sealed class OnboardingMetricsContributor : IOnboardingMetrics, IMetricsContributor
{
    private Counter<long> _volunteersApproved = null!;
    private Counter<long> _membersSuspended = null!;

    public void Initialize(IHumansMetrics metrics)
    {
        _volunteersApproved = metrics.CreateCounter<long>(
            "humans.volunteers_approved_total",
            description: "Total volunteers approved");
        _membersSuspended = metrics.CreateCounter<long>(
            "humans.members_suspended_total",
            description: "Total member suspensions");
    }

    public void RecordVolunteerApproved() => _volunteersApproved.Add(1);

    public void RecordMemberSuspended(string source) =>
        _membersSuspended.Add(1, new KeyValuePair<string, object?>("source", source));
}
