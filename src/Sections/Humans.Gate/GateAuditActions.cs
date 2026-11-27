using Humans.AuditLog.Contracts;

namespace Humans.Gate;

/// <summary>The audit actions Gate writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class GateAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition GateStaffPinSet = new("GateStaffPinSet", AuditKept.Forever);

    public static readonly AuditActionDefinition GateStaffPinReset = new("GateStaffPinReset", AuditKept.Forever);

    public static readonly AuditActionDefinition GateAdmitUndone = new("GateAdmitUndone", AuditKept.Forever);
}
