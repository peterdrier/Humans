using Humans.AuditLog.Contracts;

namespace Humans.Agent;

/// <summary>The audit actions Agent writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class AgentAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition AgentSettingsUpdated = new("AgentSettingsUpdated");
}
