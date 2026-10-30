using Humans.AuditLog.Contracts;

namespace Humans.Onboarding;

/// <summary>The audit actions Onboarding writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class OnboardingAuditActions : AuditActionContributor
{
    // Retired: nothing writes it since admission became automatic; declared so stored rows keep their verb.
    public static readonly AuditActionDefinition VolunteerApproved = new("VolunteerApproved", Verb: "approved");

    public static readonly AuditActionDefinition SignupRejected = new("SignupRejected", Verb: "rejected signup for");
}
