using System.Reflection;
using Humans.Base.Interfaces;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// One kind of audited action. The section that writes it declares it once, as a static field on its
/// <see cref="AuditActionContributor"/>, and passes it to <see cref="IAuditLogService.LogAsync(AuditActionDefinition, string, Guid, string, Guid, Guid?, string?)"/>.
/// </summary>
/// <param name="Name">Stored on every row, so never change it once rows exist. Unique across sections, at most <see cref="NameMaxLength"/> characters.</param>
/// <param name="Verb">English narrative verb ("added", "assigned role to"); null renders the entry as actor · label · subject instead of a sentence. The hub resx may carry translations under <c>AuditLog_Verb_{Name}</c>.</param>
/// <param name="SelfVerb">The verb when the actor is the subject ("signed up for" instead of "created signup for").</param>
/// <param name="DescriptionIsTail">True when the description is a context tail to append after the sentence, not a sentence of its own.</param>
/// <param name="Alert">Rows render highlighted and the admin page counts them.</param>
/// <param name="QuickFilter">A button label on the admin audit page that filters to this action; null for none.</param>
public sealed record AuditActionDefinition(
    string Name,
    string? Verb = null,
    string? SelfVerb = null,
    bool DescriptionIsTail = false,
    bool Alert = false,
    string? QuickFilter = null)
{
    public const int NameMaxLength = 100;

    public override string ToString() => Name;
}

/// <summary>
/// A section's audit actions. AuditLog collects every registered contributor at startup into one registry, which
/// rejects duplicate or over-long names and resolves stored names back to their definitions for rendering.
/// </summary>
public interface IAuditActionContributor : IFanout
{
    IReadOnlyList<AuditActionDefinition> Actions { get; }
}

/// <summary>
/// Derive from this, declare each action as a <c>public static readonly</c> field, and register the class as an
/// <see cref="IAuditActionContributor"/> singleton. The fields are the contributed list, so nothing is declared twice.
/// </summary>
public abstract class AuditActionContributor : IAuditActionContributor
{
    public IReadOnlyList<AuditActionDefinition> Actions { get; }

    protected AuditActionContributor()
    {
        Actions = GetType()
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<AuditActionDefinition>()
            .ToList();
    }
}
