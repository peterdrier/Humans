using Humans.Base.Interfaces;

namespace Humans.Issues.Contracts;

/// <summary>
/// A section's declaration that it owns an issue queue: the <c>Issue.Section</c> key its
/// issues are filed under, and the role(s) whose holders handle them (Admin is implicit on
/// every queue and is never listed here). Each owning section implements this on its own
/// <c>Section</c> entry point, so Issues holds no list of participating sections — it
/// iterates what DI discovered. A section that stops owning a queue drops the seam, and the
/// keys stored on its old rows fall through to the Admin-only queue with no migration. The
/// seam doubles as the queue's <see cref="ISectionAnnotations"/> publication, so
/// <c>/Debug/Sections</c> still shows where a reported issue lands.
/// </summary>
public interface IIssueQueueOwner : ISectionAnnotations
{
    /// <summary>The canonical <c>Issue.Section</c> value, normally the section's own name.</summary>
    string QueueKey { get; }

    /// <summary>The roles, besides Admin, that own the queue.</summary>
    IReadOnlyList<string> OwningRoles { get; }

    IEnumerable<SectionAnnotation> ISectionAnnotations.Annotations() =>
        [new SectionAnnotation(QueueKey, "Issue queue", string.Join(", ", OwningRoles))];
}
